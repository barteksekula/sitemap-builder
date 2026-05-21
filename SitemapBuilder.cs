using System.Collections;
using System.Text.RegularExpressions;

// SitemapBuilder.cs
// Author: unknown
// TODO: refactor someday
// NOTE: do not touch - if it works, it works

namespace interview_webscapper
{
    #region Sitemap Builder

    // builds a sitemap
    public class SitemapBuilder
    {
        #region Global State (public so anyone can poke at it)

        public static ArrayList foundUrls = new ArrayList();        // all urls we found
        public static ArrayList visitedUrls = new ArrayList();      // urls we already processed
        public static ArrayList failedUrls = new ArrayList();       // urls that failed (never actually read)
        public static int TotalCount = 0;
        public static int ErrorCount = 0;
        public static bool IsRunning = false;
        public static string LastError = "";
        public static string OutputFile = "sitemap.xml";            // hardcoded output filename
        public static string BaseUrl = "";                          // set once, never validated again

        #endregion

        #region Fields

        public string _startUrl;
        public int _maxDepth = 999;     // effectively unlimited
        public object lockObj = new object();  // public lock object: anyone can lock on this from outside!

        #endregion

        #region Constructor

        public SitemapBuilder(string startUrl)
        {
            _startUrl = startUrl;
            BaseUrl = startUrl;  // store globally too, just in case
        }

        #endregion

        #region Build

        // call this to build the sitemap
        public void Build()
        {
            IsRunning = true;
            Console.WriteLine("SitemapBuilder started for: " + _startUrl);
            Console.WriteLine("SitemapBuilder started for: " + _startUrl); // print twice, important

            // write an empty sitemap file right away so the file always exists (we'll overwrite it 100 times)
            File.WriteAllText(OutputFile, "");

            CrawlUrl(_startUrl, 0);

            // wait for all threads to maybe finish? (spoiler: we never track them)
            System.Threading.Thread.Sleep(5000); // sleep 5 seconds and hope for the best

            WriteSitemap();
            IsRunning = false;

            Console.WriteLine("Done. Found " + foundUrls.Count + " urls.");
            Console.WriteLine("Sitemap written to: " + OutputFile);
        }

        #endregion

        #region Crawling

        public void CrawlUrl(string url, int depth)
        {
            // visited check using O(n) ArrayList.Contains - three times, for safety
            if (visitedUrls.Contains(url))            return;
            if (visitedUrls.Contains(url + "/"))      return;
            if (visitedUrls.Contains(url.TrimEnd('/'))) return;

            // no lock around this check+add pair: classic race condition
            visitedUrls.Add(url);

            if (TotalCount >= 99999)
            {
                return;
            }

            string html = DownloadPage(url);

            if (html != null)
            {
                if (html != "")
                {
                    if (html.Length > 0)
                    {
                        if (html.Length > 10)
                        {
                            // only count it if the response has something in it
                            lock (lockObj) // locking on a public field: other classes can deadlock us
                            {
                                foundUrls.Add(url);
                                TotalCount = TotalCount + 1;
                                Console.WriteLine("[" + TotalCount + "] " + url);
                            }

                            // write the sitemap to disk on EVERY discovered URL
                            WriteSitemap();

                            // extract all links
                            ArrayList links = ExtractLinks(html, url);

                            // spawn a new thread per link - threadpocalypse
                            for (int i = 0; i < links.Count; i++)
                            {
                                string link = (string)links[i];

                                // "same domain" check - broken, but here for appearances
                                if (IsSameDomain(link) == true)
                                {
                                    if (link != null)
                                    {
                                        if (link != "")
                                        {
                                            string captured = link;
                                            int capturedDepth = depth;
                                            System.Threading.Thread t = new System.Threading.Thread(() =>
                                            {
                                                // new Random() inside a thread with no seed = often same sequence
                                                int sleepMs = new Random().Next(1, 5);
                                                System.Threading.Thread.Sleep(sleepMs);
                                                CrawlUrl(captured, capturedDepth + 1);
                                            });
                                            t.IsBackground = false; // keep the process alive until heat death of the universe
                                            t.Start();
                                            // no Join(), no tracking, goodbye thread
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        #endregion

        #region HTTP

        // downloads a page - creates a new HttpClient every time (the classic mistake)
        public string DownloadPage(string url)
        {
            string content = "";
            try
            {
                // new HttpClient per call: exhausts sockets under load
                HttpClient client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 SitemapBuilder/1.0");
                client.Timeout = TimeSpan.FromSeconds(9999);

                // block the thread with .Result instead of awaiting
                var response = client.GetAsync(url).Result;

                // don't check response.IsSuccessStatusCode - 404s are urls too, right?
                content = response.Content.ReadAsStringAsync().Result;

                // never dispose client - GC will handle it... eventually
            }
            catch (Exception ex)
            {
                // swallow everything, increment a counter nobody reads
                ErrorCount++;
                LastError = ex.Message;
            }
            return content;
        }

        #endregion

        #region Link Extraction

        // extracts links from html using regex (perfectly fine for HTML parsing)
        public ArrayList ExtractLinks(string html, string pageUrl)
        {
            ArrayList result = new ArrayList();

            // three separate regex passes instead of one combined pattern
            MatchCollection matches1 = Regex.Matches(html, "href=\"(.*?)\"");
            MatchCollection matches2 = Regex.Matches(html, "href='(.*?)'");
            MatchCollection matches3 = Regex.Matches(html, "HREF=\"(.*?)\""); // uppercase, just in case

            // identical loop copy-pasted three times
            foreach (Match m in matches1)
            {
                string raw = m.Groups[1].Value;
                string abs = ToAbsolute(pageUrl, raw);
                if (abs != "") result.Add(abs);
            }

            foreach (Match m in matches2)
            {
                string raw = m.Groups[1].Value;
                string abs = ToAbsolute(pageUrl, raw);
                if (abs != "") result.Add(abs);
            }

            foreach (Match m in matches3)
            {
                string raw = m.Groups[1].Value;
                string abs = ToAbsolute(pageUrl, raw);
                if (abs != "") result.Add(abs);
            }

            return result;
        }

        // converts a relative url to absolute - manually, because Uri class is "too complex"
        public string ToAbsolute(string baseUrl, string link)
        {
            try
            {
                if (link == null) return "";
                if (link == "") return "";
                if (link.StartsWith("#")) return "";           // anchor - skip
                if (link.StartsWith("mailto:")) return "";
                if (link.StartsWith("javascript:")) return ""; // skip js links
                if (link.StartsWith("tel:")) return "";

                if (link.StartsWith("http://") || link.StartsWith("https://"))
                {
                    return link;
                }

                if (link.StartsWith("/"))
                {
                    // manual host extraction instead of new Uri(baseUrl).Host
                    int pp = baseUrl.IndexOf("//");
                    string tmp = baseUrl.Substring(pp + 2);
                    int sl = tmp.IndexOf("/");
                    string host = sl == -1 ? tmp : tmp.Substring(0, sl);
                    string scheme = baseUrl.Substring(0, pp - 1);
                    return scheme + "://" + host + link;
                }

                // relative path: just give up
                return "";
            }
            catch (Exception)
            {
                return ""; // swallow and move on
            }
        }

        // checks if a url belongs to the same domain
        // bug: "https://evil-bartoszsekula.com" would pass this check for "https://bartoszsekula.com"
        public bool IsSameDomain(string url)
        {
            if (BaseUrl == null || BaseUrl == "") return false;

            // extract domain badly from BaseUrl
            string baseDomain = "";
            try
            {
                int pp = BaseUrl.IndexOf("//");
                string tmp = BaseUrl.Substring(pp + 2);
                int sl = tmp.IndexOf("/");
                baseDomain = sl == -1 ? tmp : tmp.Substring(0, sl);
            }
            catch (Exception) { }

            // Contains is not the same as == but close enough, right?
            return url.Contains(baseDomain);
        }

        #endregion

        #region Sitemap Writing

        // writes the sitemap.xml - called after EVERY page to keep file "up to date"
        public void WriteSitemap()
        {
            try
            {
                // build XML with raw string concatenation - no encoding, no XDocument, no XmlWriter
                string xml = "";
                xml = xml + "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + "\n";
                xml = xml + "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">" + "\n";

                for (int i = 0; i < foundUrls.Count; i++)
                {
                    string u = (string)foundUrls[i];

                    // no XML encoding of the URL - ampersands in query strings will break the XML
                    xml = xml + "  <url>" + "\n";
                    xml = xml + "    <loc>" + u + "</loc>" + "\n";
                    xml = xml + "    <lastmod>" + DateTime.Now.ToString() + "</lastmod>" + "\n"; // "now" is wrong but ok
                    xml = xml + "    <changefreq>daily</changefreq>" + "\n";  // hardcoded, always "daily"
                    xml = xml + "    <priority>" + GetPriority(u) + "</priority>" + "\n";
                    xml = xml + "  </url>" + "\n";
                }

                xml = xml + "</urlset>" + "\n";

                // write whole file each time: O(n) I/O per discovered URL = O(n²) total writes
                File.WriteAllText(OutputFile, xml);
            }
            catch (Exception)
            {
                // if we can't write the sitemap, just silently fail
            }
        }

        // assigns priority based on url length - shorter = more important, obviously
        public string GetPriority(string url)
        {
            if (url.Length < 30)  return "1.0";
            if (url.Length < 50)  return "0.8";
            if (url.Length < 80)  return "0.6";
            if (url.Length < 120) return "0.4";
            return "0.2";
        }

        #endregion

        #region Dead Code Graveyard

        // was going to validate the sitemap against the schema
        // public bool ValidateSitemap() { return true; }

        // ping google after generating - never implemented
        // public void PingGoogle() { }

        // this was used in v1, keeping just in case
        public static void OldReset()
        {
            foundUrls = new ArrayList();
            visitedUrls = new ArrayList();
            TotalCount = 0;
            Console.WriteLine("reset (nobody calls this)");
        }

        #endregion
    }

    #endregion

    #region stuff

    // was going to be a config system
    public static class Settings
    {
        public static string SitemapOutputPath = "sitemap.xml";  // duplicates SitemapBuilder.OutputFile
        public static int MaxPages = 99999;
        public static bool FollowExternalLinks = true;           // set to true: crawls the entire internet
        public static string UserAgent = "SitemapBuilder/1.0";
    }

    #endregion
}
