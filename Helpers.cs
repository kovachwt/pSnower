using Newtonsoft.Json;
using Spectre.Console;
using Spectre.Console.Rendering;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace pSnower
{

    public class pSnowSettings
    {
        // can be changed in settings.json
        public string TwitchAppClientID = "";
        public string TwitchAppClientSecret = "";
        public string TwitchAppToken = "";

        public string AllPSNowGamesUrl = "https://www.pushsquare.com/guides/all-playstation-now-games";
        public string CoopGamesUrl = "https://www.co-optimus.com/ajax/ajax_games.php?game-title-filter=&system=22&countDirection=at%20least&playerCount=2&sort=&sortDirection=";
    }

    public class CoopGame
    {
        public string Id { get; set; }
        public string Shortname { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public string Num_online { get; set; }
        public string Num_couch { get; set; }
        public string Num_combo { get; set; }
        public string Score_overall { get; set; }
        public string Score_coop { get; set; }
        public string Released { get; set; }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this, Formatting.None);
        }
    }

    public static class Helpers
    {

        public static string compareClean(string name)
        {
            return Regex.Replace(RemoveAccents(name), @"[^\w0-9]", "", RegexOptions.Singleline)
                .Replace("2", "ii").Replace("3", "iii").Replace("4", "iv").Replace("5", "v").Replace("6", "vi")
                .ToLower();
        }

        public static string RemoveAccents(string input)
        {
            return new string(input
                .Normalize(System.Text.NormalizationForm.FormD)
                .ToCharArray()
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                .ToArray());
            // the normalization to FormD splits accented letters in letters+accents
            // the rest removes those accents (and other non-spacing characters)
            // and creates a new string from the remaining chars
        }

        public static string formatLongDate(string date)
        {
            if (("" + date).Trim() == "") return "";

            string[] expectedFormats = new[]
            {
                "d'st' MMMM, yyyy",
                "d'nd' MMMM, yyyy",
                "d'rd' MMMM, yyyy",
                "d'th' MMMM, yyyy"
            };

            if (DateTime.TryParseExact(date, expectedFormats, new CultureInfo("en-US"), DateTimeStyles.AssumeLocal, out DateTime result))
                return result.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return date;
        }

        public static DateTime UnixTimeToDateTime(int unixtime)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unixtime);
        }


        /// <summary>
        /// Adds multiple rows to the table.
        /// </summary>
        /// <typeparam name="T">The item type.</typeparam>
        /// <param name="table">The table to add the row to.</param>
        /// <param name="values">The values to create rows from.</param>
        /// <param name="columnFunc">A collection of functions that gets a column value.</param>
        /// <returns>The same instance so that multiple calls can be chained.</returns>
        public static Table AddRows<T>(this Table table, IEnumerable<T> values, params Func<T, object>[] columnFunc)
        {
            if (table is null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            if (values is null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            foreach (var value in values)
            {
                var columns = new List<IRenderable>();
                foreach (var converter in columnFunc)
                {
                    var column = converter(value);
                    if (column == null)
                    {
                        columns.Add(Text.Empty);
                    }
                    else if (column is IRenderable renderable)
                    {
                        columns.Add(renderable);
                    }
                    else
                    {
                        var text = column?.ToString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            columns.Add(Text.Empty);
                        }
                        else
                        {
                            columns.Add(new Markup(text));
                        }
                    }
                }

                table.AddRow(columns.ToArray());
            }

            return table;
        }

    }
}
