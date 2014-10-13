using System;
using System.Collections.Generic;

using System.Linq;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;

using Twilio;
using Twilio.TwiML;
using Twilio.TwiML.Mvc;
using System.Drawing;
using ZXing;
using Nutritionix;
using System.Net.Http;
using System.Text;

namespace BarcodeNutritionMMS.Controllers
{
    public class BarcodeNutritionController : Controller
    {
        private string _nutritionixId = Environment.GetEnvironmentVariable("NutritionixAppId");
        private string _nutritionixKey = Environment.GetEnvironmentVariable("NutritionixKey");

        public async Task<ActionResult> Inbound(int numMedia, string body)
        {
            var response = new TwilioResponse();
            body = body.Trim();

            if (numMedia == 0)
            {
                response.Message("You didn't send any barcodes! Please send in well-focused and zoomed in barcodes with the word 'total' to get total nutrition values or 'compare' to get a comparison of the items.");
                return new TwiMLResult(response);
            }

            string[] eanCodes = await DecodeBarcodes(numMedia);

            if (eanCodes == null)
            {
                // There was an error with one of the barcodes. Bail so user can try again.
                response.Message("One of your barcodes was not recognized. Please try cropping your image or taking the picture again closer to the barcode.");
                return new TwiMLResult(response);
            }

            // Get nutrition info for EAN codes
            List<Item> foodItems = new List<Item>();
            List<string> skippedBarcodes = new List<string>();

            LookupNutritionInfo(eanCodes, ref foodItems, ref skippedBarcodes);

            if (skippedBarcodes.Count > 0)
            {
                // Let's tell the users that we couldn't find their item(s)...
                var builder = new StringBuilder();

                builder.Append("Sorry but we couldn't find one or more of your items. Please try again without the following EANs which were not found in the Nutritionix database: ");

                foreach (var barcode in skippedBarcodes)
                {
                    builder.Append(barcode + " ");
                }

                response.Message(builder.ToString());
                return new TwiMLResult(response);
            }

            string responseString = GetNutritionInfoResponse(numMedia, body, foodItems, eanCodes);

            response.Message(responseString + "\n\nPowered by Twilio.");
            return new TwiMLResult(response);
        }

        private void LookupNutritionInfo(string[] eanCodes, ref List<Item> foodItems, ref List<string> skippedBarcodes)
        {
            var nutritionix = new NutritionixClient();
            nutritionix.Initialize(_nutritionixId, _nutritionixKey);

            foodItems = new List<Item>(eanCodes.Length);
            skippedBarcodes = new List<string>();

            foreach (var barcode in eanCodes)
            {
                Item food = null;

                try
                {
                    food = nutritionix.RetrieveItemByUPC(barcode);
                }
                catch
                {
                    // Invalid barcode format results in a 404. We'll add it to the skipped barcodes list.
                    skippedBarcodes.Add(barcode);
                    return;
                }

                if (food != null)
                {
                    foodItems.Add(food);
                }
                else
                {
                    // One of the food items is not available in Nutritionix.
                    skippedBarcodes.Add(barcode);
                }
            }
        }

        private string GetNutritionInfoResponse(int numMedia, string keyword, List<Item> foodItems, string[] eanCodes)
        {
            string responseString = "";

            // Depending on number of items and the keyword in the Body, run some nutrition calculations
            if (numMedia == 1)
            {
                // Single item, just return details for that item.
                responseString = GetSingleItemNutrition(foodItems[0]);
            }
            else if (keyword == String.Empty)
            {
                // Default to totals
                responseString = GetTotalNutrition(foodItems, eanCodes);
            }
            else if (String.Equals(keyword, "total", StringComparison.OrdinalIgnoreCase))
            {
                // User explicitly requested total nutrition info
                responseString = GetTotalNutrition(foodItems, eanCodes);
            }
            else if (String.Equals(keyword, "compare", StringComparison.OrdinalIgnoreCase))
            {
                // User requested item comparison
                responseString = CompareNutrition(foodItems, eanCodes);
            }
            else
            {
                // Invalid keyword
                responseString = String.Format("You sent in '{0}' which is not a valid keyword. Please send in well-focused and zoomed in barcodes with the word 'total' to get total nutrition values or 'compare' to get a comparison of the items.", keyword);
            }

            return responseString;
        }

        private async Task<string[]> DecodeBarcodes(int numberOfImages)
        {
            // Build a List<Bitmap> from incoming images
            var images = new List<Bitmap>(numberOfImages);

            // Build an array of EAN codes from reading the barcodes
            var eanCodes = new string[numberOfImages];

            var httpClient = new HttpClient();
            var reader = new BarcodeReader();

            for (int i = 0; i < numberOfImages; i++)
            {
                Bitmap bitmap;

                using (var stream = await httpClient.GetStreamAsync(Request["MediaUrl" + i]))
                {
                    using (bitmap = (Bitmap)Bitmap.FromStream(stream))
                    {
                        var result = reader.Decode(bitmap);

                        if (result == null)
                        {
                            // Couldn't read this barcode, we'll return null to indicate we should bail...
                            return null;
                        }

                        eanCodes[i] = result.Text;
                    }
                }
            }

            return eanCodes;
        }

        private string GetSingleItemNutrition(Item foodItem)
        {
            return String.Format(
                    "Here are the totals for {0} {1}: {2} calories, {3}g protein, {4}g total carbohydrates, {5}g total fat.",
                    foodItem.BrandName,
                    foodItem.Name,
                    foodItem.NutritionFact_Calories,
                    foodItem.NutritionFact_Protein,
                    foodItem.NutritionFact_TotalCarbohydrate,
                    foodItem.NutritionFact_TotalFat
                );
        }
        private string CompareNutrition(List<Item> foodItems, string[] eanCodes)
        {
            var lowestCalories = foodItems.Aggregate(
                                        (item1, item2) => item1.NutritionFact_Calories < item2.NutritionFact_Calories ? item1 : item2
                                 );

            var highestProtein = foodItems.Aggregate(
                                        (item1, item2) => item1.NutritionFact_Protein > item2.NutritionFact_Protein ? item1 : item2
                                 );

            var lowestCarbs = foodItems.Aggregate(
                                        (item1, item2) => item1.NutritionFact_TotalCarbohydrate < item2.NutritionFact_TotalCarbohydrate ? item1 : item2
                                 );

            var lowestFat = foodItems.Aggregate(
                                        (item1, item2) => item1.NutritionFact_TotalFat < item2.NutritionFact_TotalFat ? item1 : item2
                                 );

            return string.Format(
                "Lowest calories: {0} {1} (barcode: {2}) with {3} calories. Highest protein: {4} {5} (barcode: {6}) with {7}g of protein. Lowest total carbs: {8} {9} (barcode: {10}) with {11}g carbs. Lowest total fat: {12} {13} (barcode: {14}) with {15}g fat.",
                lowestCalories.BrandName,
                lowestCalories.Name,
                eanCodes[foodItems.IndexOf(lowestCalories)],
                lowestCalories.NutritionFact_Calories,
                highestProtein.BrandName,
                highestProtein.Name,
                eanCodes[foodItems.IndexOf(highestProtein)],
                highestProtein.NutritionFact_Protein,
                lowestCarbs.BrandName,
                lowestCarbs.Name,
                eanCodes[foodItems.IndexOf(lowestCarbs)],
                lowestCarbs.NutritionFact_TotalCarbohydrate,
                lowestFat.BrandName,
                lowestFat.Name,
                eanCodes[foodItems.IndexOf(lowestFat)],
                lowestFat.NutritionFact_TotalFat
            );
        }

        private static string GetTotalNutrition(List<Item> foodItems, string[] eanCodes)
        {
            // Default to returning total nutrition info
            var totalCalories = foodItems.Sum((item) => item.NutritionFact_Calories).Value;
            var totalProtein = foodItems.Sum((item) => item.NutritionFact_Protein).Value;
            var totalCarbs = foodItems.Sum((item) => item.NutritionFact_TotalCarbohydrate).Value;
            var totalFat = foodItems.Sum((item) => item.NutritionFact_TotalFat).Value;

            return string.Format("Here are the totals for the items you requested: {0} calories, {1}g protein, {2}g carbohydrates and {3}g total fat.", totalCalories, totalProtein, totalCarbs, totalFat);

        }
    }
}