using System;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace SKKPedigree.Scraper
{
    /// <summary>
    /// Manages a Playwright browser session for scraping hundar.skk.se/hunddata/.
    /// The SKK site uses ASP.NET WebForms with __VIEWSTATE and __doPostBack;
    /// Playwright handles all of that transparently by running a real Chromium browser.
    /// </summary>
    public class SkkSession : IAsyncDisposable
    {
        private const string BaseUrl   = "https://hundar.skk.se/hunddata/";
        private const string SearchUrl = "https://hundar.skk.se/hunddata/Hund_sok.aspx";

        private IPlaywright? _playwright;
        private IBrowser? _browser;
        private IPage? _page;
        private readonly int _requestDelayMs;

        public SkkSession(int requestDelayMs = 1500)
        {
            _requestDelayMs = requestDelayMs;
        }

        public async Task InitAsync(bool headless = true)
        {
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless
            });
            var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 SKKPedigreeBrowser/1.0 (personal research)"
            });
            _page = await context.NewPageAsync();

            await _page.GotoAsync(SearchUrl);
            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        /// <summary>
        /// Searches for a dog by registration number and returns the result page HTML.
        /// If multiple results are returned, returns the HTML of the search result list.
        /// </summary>
        public async Task<string> GetDogPageHtmlAsync(string regNumber)
        {
            EnsureInitialised();
            await _page!.GotoAsync(SearchUrl);
            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // Search by registration number
            await _page.FillAsync("#bodyContent_txtRegnr", regNumber);
            await _page.ClickAsync("#bodyContent_btnSearch");
            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Task.Delay(_requestDelayMs);

            return await _page.ContentAsync();
        }

        /// <summary>
        /// Navigates to a dog detail page by its relative URL and returns the full page HTML.
        /// </summary>
        public async Task<string> GetDogDetailHtmlAsync(string relativeUrl)
        {
            EnsureInitialised();
            var absoluteUrl = relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? relativeUrl
                : new Uri(new Uri(BaseUrl), relativeUrl).ToString();

            await _page!.GotoAsync(absoluteUrl);
            await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Task.Delay(_requestDelayMs);

            return await _page.ContentAsync();
        }

        private void EnsureInitialised()
        {
            if (_page == null)
                throw new InvalidOperationException(
                    "SkkSession is not initialised. Call InitAsync() first.");
        }

        /// <summary>
        /// Calls Hund_sok.aspx/HundData directly via fetch inside the browser page
        /// (inherits session cookies automatically). Returns the total match count
        /// and up to 300 hundid values. If Total > 300 the caller must subdivide.
        /// </summary>
        public async Task<(int Total, List<int> Ids)> SearchApiAsync(
            string namePrefix, string breedId = "")
        {
            EnsureInitialised();

            // Make sure we're on the search page so cookies/session are valid
            if (!_page!.Url.Contains("Hund_sok", StringComparison.OrdinalIgnoreCase))
            {
                await _page.GotoAsync(SearchUrl);
                await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            }

            var result = await _page.EvaluateAsync<System.Text.Json.JsonElement>(
                @"async ([prefix, breed]) => {
                    try {
                        const body = JSON.stringify({
                            txtRegnr: '', txtIDnummer: '', txtChipnr: '',
                            txtHundnamn: prefix,
                            ddlRasIn: breed,
                            ddlKon: '', txtLicensnr: ''
                        });
                        const resp = await fetch('Hund_sok.aspx/HundData', {
                            method: 'POST',
                            headers: {'Content-Type': 'application/json;charset=utf-8'},
                            body: body
                        });
                        const json = await resp.json();
                        const d = json.d || [];
                        const total = d.length > 0 ? (parseInt(d[0].Antal) || d.length) : 0;
                        const ids = d.map(x => x.hundid).filter(x => x > 0);
                        return { total: total, ids: ids };
                    } catch(e) {
                        return { total: 0, ids: [] };
                    }
                }",
                new object[] { namePrefix, breedId });

            var total = result.GetProperty("total").GetInt32();
            var ids   = result.GetProperty("ids")
                              .EnumerateArray()
                              .Select(x => x.GetInt32())
                              .ToList();
            return (total, ids);
        }

        /// <summary>
        /// Re-initialises the session if it has expired or errored.
        /// </summary>
        public async Task ResetAsync(bool headless = true)
        {
            if (_browser != null)
            {
                await _browser.CloseAsync();
                _browser = null;
            }
            _playwright?.Dispose();
            _playwright = null;
            _page = null;
            await InitAsync(headless);
        }

        public async ValueTask DisposeAsync()
        {
            if (_browser != null) await _browser.CloseAsync();
            _playwright?.Dispose();
        }
    }
}
