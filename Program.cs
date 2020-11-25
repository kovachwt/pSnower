using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using HtmlAgilityPack;
using McMaster.Extensions.CommandLineUtils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Spectre.Console;

namespace pSnower
{
    class Program
    {
        static pSnowSettings Settings = new pSnowSettings();

        static Dictionary<string, string> untilDates = new Dictionary<string, string>();
        static List<CoopGame> allCoopGames = new List<CoopGame>();

        static int Main(string[] args)
        {
            var app = new CommandLineApplication();

            app.HelpOption();
            var optionLocalCoop = app.Option("-lc|--local-coop", "only show local multiplayer couch coop games", CommandOptionType.NoValue);
            var optionPS4only = app.Option("-dl|--downloadable", "only show downloadable PS4 games, filter out streaming games", CommandOptionType.NoValue);
            var optionNumGames = app.Option<int>("-n|--count <N>", "how many games to display in the list (default 100)", CommandOptionType.SingleValue);
            var optionSort = app.Option("-s|--sort <d/u/c/b>", "sort by release [d]ate, [u]ser reviews score, [c]ritic reviews score, or [b]oth scores combined (default)", CommandOptionType.SingleValue);
            var optionNumRatings = app.Option<int>("-m|--min-ratings <N>", "minimum number of ratings a game must have to be counted (default 3)", CommandOptionType.SingleValue);

            app.OnExecute(() =>
            {
                AnsiConsole.WriteLine("Starting...");

                string rootfolder = AppDomain.CurrentDomain.BaseDirectory?.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

                string setfile = rootfolder + "settings.json";
                if (!File.Exists(setfile))
                    File.WriteAllText(setfile, JsonConvert.SerializeObject(Settings, Formatting.Indented));
                if (File.Exists(setfile))
                    Settings = JsonConvert.DeserializeObject<pSnowSettings>(File.ReadAllText(setfile));



                string allgamesfile = rootfolder + "allnowgames.txt";
                var games = GetNowGames(allgamesfile);
                AnsiConsole.WriteLine($"Found {games.Count} pSnow games.");


                string dbfile = rootfolder + "gamedb.json";
                var gamedb = GetAllGamesInfo(dbfile, games, true).Result;

                AnsiConsole.WriteLine($"Found {gamedb.Count} total games in IGDB.");


                string couchgamesfile = rootfolder + "couchgames.json";
                allCoopGames = GetCoopGamesList(couchgamesfile, "&couch=true").Result;

                string splitscreengamesfile = rootfolder + "splitscreengames.json";
                var splitscreen = GetCoopGamesList(splitscreengamesfile, "&splitscreen=true").Result;

                foreach (var game in splitscreen)
                    if (allCoopGames.Count(c => c.Name == game.Name) == 0)
                        allCoopGames.Add(game);
                


                int numratings = optionNumRatings.HasValue() ? optionNumRatings.ParsedValue : 3;
                int takenumgames = optionNumGames.HasValue() ? optionNumGames.ParsedValue : 100;

                bool sortdate = optionSort.HasValue() && optionSort.Value().ToLower() == "d";
                bool sortcombined = !optionSort.HasValue() || optionSort.Value().ToLower() == "c";
                bool sortuser = optionSort.HasValue() && optionSort.Value().ToLower() == "u";


                var topgames = gamedb
                    .Where(g => (!optionLocalCoop.HasValue() || isLocalMulti((string)g["name"])) && (!optionPS4only.HasValue() || isPS4game(g)));

                if (sortdate)
                    topgames = topgames
                        .Where(g => g["first_release_date"] != null)
                        .OrderByDescending(g => (int)g["first_release_date"]);
                else if (sortcombined)
                    topgames = topgames
                        .Where(g => g["aggregated_rating"] != null && (int)g["aggregated_rating_count"] >= numratings && g["rating"] != null && (int)g["rating_count"] >= numratings)
                        .OrderByDescending(g => getCombinedRating(g));
                else if (sortuser)
                    topgames = topgames
                        .Where(g => g["rating"] != null && (int)g["rating_count"] >= numratings)
                        .OrderByDescending(g => (decimal)g["rating"]);
                else 
                    topgames = topgames
                        .Where(g => g["aggregated_rating"] != null && (int)g["aggregated_rating_count"] >= numratings)
                        .OrderByDescending(g => (decimal)g["aggregated_rating"]);

                ShowResults(topgames.Take(takenumgames));
            });

            return app.Execute(args);
        }

        static List<string> GetNowGames(string allgamesfile)
        {
            if (File.Exists(allgamesfile + ".until"))
                untilDates = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(allgamesfile + ".until"));

            if (File.Exists(allgamesfile))
                return File.ReadAllLines(allgamesfile).ToList();

            HtmlWeb web = new HtmlWeb();
            var htmlDoc = web.Load(Settings.AllPSNowGamesUrl);
            var lis = htmlDoc.DocumentNode.SelectNodes("//section[@class='text']/ul/li");
            var games = lis.Select(li => WebUtility.HtmlDecode(li.InnerText).Trim()).ToList();

            games = games.Select(game => cleanGameName(game)).ToList();

            File.WriteAllLines(allgamesfile, games.ToArray());

            File.WriteAllText(allgamesfile + ".until", JsonConvert.SerializeObject(untilDates));

            return games;
        }

        static async Task<JArray> GetAllGamesInfo(string filename, List<string> gamenames, bool onlylocal = false)
        {
            JArray existing = File.Exists(filename) ? JArray.Parse(File.ReadAllText(filename)) : new JArray();

            HttpClient cli = new HttpClient();
            cli.DefaultRequestHeaders.Add("Client-ID", Settings.TwitchAppClientID);
            cli.DefaultRequestHeaders.Add("Authorization", "Bearer " + Settings.TwitchAppToken);

            JArray result = new JArray();
            int found = 0;

            DateTime beforereq = DateTime.UtcNow;
            foreach (string gamename in gamenames)
            {
                var exist = existing.Where(g => (g["key_name"] != null && (string)g["key_name"] == gamename) || g.Value<string>("name").Trim().ToLower() == gamename.ToLower()).FirstOrDefault();
                if (exist != null)
                {
                    result.Add(exist);
                    found++;
                }
                else if (!onlylocal)
                {
                    var game = await GetGameInfo(cli, gamename);
                    if (game != null)
                    {
                        game["key_name"] = gamename;
                        result.Add(game);
                        AnsiConsole.WriteLine("Found game in IGDB: " + gamename + " == " + game["name"]);
                    }
                    else
                    {
                        AnsiConsole.WriteLine("Game NOT FOUND in IGDB: " + gamename);
                    }

                    var diff = DateTime.UtcNow - beforereq;
                    if (diff < TimeSpan.FromSeconds(0.3))
                        System.Threading.Thread.Sleep((int)(TimeSpan.FromSeconds(0.3) - diff).TotalMilliseconds + 10);
                    beforereq = DateTime.UtcNow;
                }
            }

            File.WriteAllText(filename, result.ToString());
            AnsiConsole.WriteLine($"Found {found} games already in local DB. Successfully retrieved {result.Count - found} more.");
            return result;
        }


        static async Task<JObject> GetGameInfo(HttpClient cli, string name)
        {
            string query = $"fields *; search \"{name}\"; where release_dates.platform = (8,9,45,48);";     // search ps2, ps3, psn, ps4
            var resp = await cli.PostAsync("https://api.igdb.com/v4/games", new ByteArrayContent(Encoding.UTF8.GetBytes(query)));
            var jsonres = await resp.Content.ReadAsStringAsync();
            var resgames = JArray.Parse(jsonres);

            if (resgames.Count == 0) return null;

            var jgame = resgames.Where(g => g.Value<string>("name").Trim().ToLower() == name.ToLower()).FirstOrDefault();
            if (jgame != null) return jgame as JObject;

            jgame = resgames.Where(g => Helpers.compareClean(g.Value<string>("name")) == Helpers.compareClean(name)).FirstOrDefault();
            if (jgame != null) return jgame as JObject;


            jgame = resgames.Where(g => Helpers.compareClean(g.Value<string>("name")).StartsWith(Helpers.compareClean(name))).FirstOrDefault();
            if (jgame != null) return jgame as JObject;

            jgame = resgames.Where(g => Helpers.compareClean(name).StartsWith(Helpers.compareClean(g.Value<string>("name")))).FirstOrDefault();
            if (jgame != null) return jgame as JObject;


            jgame = resgames.Where(g => Helpers.compareClean(g.Value<string>("name")).EndsWith(Helpers.compareClean(name))).FirstOrDefault();
            if (jgame != null) return jgame as JObject;

            jgame = resgames.Where(g => Helpers.compareClean(name).EndsWith(Helpers.compareClean(g.Value<string>("name")))).FirstOrDefault();
            if (jgame != null) return jgame as JObject;

            return resgames.First() as JObject;
        }

        static async Task<List<CoopGame>> GetCoopGamesList(string filename, string urlextra)
        {
            if (File.Exists(filename))
                return JsonConvert.DeserializeObject<List<CoopGame>>(File.ReadAllText(filename));

            var result = new List<CoopGame>();

            int page = 1;
            do
            {
                string url = $"{Settings.CoopGamesUrl}{urlextra}&page={page}";
                var res = await new HttpClient().GetAsync(url);
                var html = await res.Content.ReadAsStringAsync();
                if (("" + html).Trim() == "")
                    break;

                html = "<html><body>" + html + "</body></html>";

                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(html);
                var rows = htmlDoc.DocumentNode.SelectNodes("//tr[@class='result_row']");
                if (rows == null || rows.Count == 0)
                    break;

                int added = 0;
                foreach (var tr in rows)
                    try
                    {
                        string id = tr.GetAttributeValue("id", "");
                        string shortname = tr.GetAttributeValue("title", "");
                        var tds = tr.SelectNodes("./td");

                        var game = new CoopGame()
                        {
                            Id = id,
                            Shortname = shortname,

                            Name = tds[0].SelectSingleNode("./strong").InnerText,
                            Category = tds[0].SelectSingleNode("./label")?.InnerText,

                            Num_online = tds[1]?.InnerText,
                            Num_couch = tds[2]?.InnerText,
                            Num_combo = tds[3]?.InnerText,

                            Score_overall = tds[5].SelectSingleNode("./div[@class='score-bar mini overall']/div")?.InnerText,
                            Score_coop = tds[5].SelectSingleNode("./div[@class='score-bar mini co-op']/div")?.InnerText,

                            Released = tds[7]?.InnerText
                        };

                        AnsiConsole.WriteLine($"Added COOP Game: {game.Name}");
                        result.Add(game);
                        added++;
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.WriteLine("Can't parse game: " + Environment.NewLine + tr.InnerHtml + Environment.NewLine + ex.ToString());
                    }

                if (added == 0)
                    break;
                page++;
            }
            while (true);

            File.WriteAllText(filename, JsonConvert.SerializeObject(result));
            AnsiConsole.WriteLine($"Saved {result.Count} total COOP games.");

            return result;
        }

        static void ShowResults(IEnumerable<JToken> values)
        {
            var table = new Table();
            table.AddColumn("Name");
            table.Columns[0].NoWrap = true;
            table.AddColumn("PS4");
            table.AddColumn("Coop");
            table.AddColumn("Until");
            table.AddColumn("Reviews");
            table.AddColumn("Rating");
            table.AddColumn("Released");
            table.AddColumn("Summary");
            table.Columns[6].NoWrap = false;

            table.AddRows(values,
                g => (string)g["name"],
                g => isPS4game(g) ? "PS4" : "stream",
                g => isLocalMulti((string)g["name"]) ? "Local" : "",
                g => untilDates.ContainsKey((string)g["name"]) ? Helpers.formatLongDate(untilDates[(string)g["name"]]) : "",
                g => g["aggregated_rating"] != null ? ((decimal)g["aggregated_rating"]).ToString("00") : "",
                g => g["rating"] != null ? ((decimal)g["rating"]).ToString("00") : "",
                g => g["first_release_date"] == null ? "" : Helpers.UnixTimeToDateTime((int)g["first_release_date"]).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                g => (("" + (string)g["summary"]).Length > 100 ? ("" + (string)g["summary"]).Substring(0, 100) : "" + (string)g["summary"]).Replace('\n', ' ').Replace('\r', ' ').Trim()
                );

            AnsiConsole.Render(table);
        }

        static decimal getCombinedRating(JToken g)
        {
            var critic = (decimal)g["aggregated_rating"];
            var numcritic = (int)g["aggregated_rating_count"];
            var user = (decimal)g["rating"];
            var numuser = (int)g["rating_count"];

            return ((critic * (numcritic * 4)) + (user * numuser)) / ((numcritic * 4) + numuser);
        }

        static bool isPS4game(JToken g)
        {
            return g["platforms"] is JArray && ((JArray)g["platforms"]).Count(p => (int)p == 48) > 0;
        }

        static bool isLocalMulti(string name)
        {
            var found = allCoopGames.Where(c => c.Name.ToLower() == name.ToLower()).SingleOrDefault();

            if (found == null)
                found = allCoopGames.Where(c => Helpers.compareClean(c.Name) == Helpers.compareClean(name)).SingleOrDefault();

            if (found == null)
                found = allCoopGames.Where(c => Helpers.compareClean(c.Name).StartsWith(Helpers.compareClean(name))).FirstOrDefault();
            if (found == null)
                found = allCoopGames.Where(c => Helpers.compareClean(name).StartsWith(Helpers.compareClean(c.Name))).FirstOrDefault();

            return found != null;
        }

        static string cleanGameName(string game)
        {
            if (game.Contains(" - Until ") &&
                (game.EndsWith(DateTime.Now.Year.ToString()) || game.EndsWith((DateTime.Now.Year + 1).ToString()) || game.EndsWith((DateTime.Now.Year + 2).ToString())))
            {
                var until = game.Substring(game.LastIndexOf(" - Until ") + " - Until ".Length).Trim();
                var name = game.Substring(0, game.LastIndexOf(" - Until ")).Trim();
                untilDates[name] = until;
                return name;
            }
            return game.Trim();
        }

    }

}