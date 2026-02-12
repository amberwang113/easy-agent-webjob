using HtmlAgilityPack;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using static DBService;
using Azure.Core;

public class WebsiteScrapingService
{
    private HttpClient httpClient;
    private HashSet<string> visitedUrls = new HashSet<string>();

    private DBService db;

    private ChatbotConfiguration config;

    private TokenCredential credential;

    private string? accessToken;
    private DateTimeOffset tokenExpiry = DateTimeOffset.MinValue;

    public WebsiteScrapingService(ChatbotConfiguration config, DBService db, TokenCredential credential)
    {
        this.httpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
        this.db = db;
        this.config = config;
        this.credential = credential;
    }

    private async Task EnsureAuthenticatedAsync()
    {
        if (string.IsNullOrEmpty(config.WEBSITE_EASYAGENT_EASYAUTH_AUDIENCE))
        {
            Console.WriteLine("EasyAuth: No audience configured — skipping authentication.");
            return;
        }

        if (accessToken != null && DateTimeOffset.UtcNow < tokenExpiry.AddMinutes(-5))
        {
            return;
        }

        var scope = config.WEBSITE_EASYAGENT_EASYAUTH_AUDIENCE.TrimEnd('/') + "/.default";
        Console.WriteLine($"EasyAuth: Requesting token with scope '{scope}'...");
        var tokenRequestContext = new TokenRequestContext([scope]);
        var token = await credential.GetTokenAsync(tokenRequestContext, CancellationToken.None);

        accessToken = token.Token;
        tokenExpiry = token.ExpiresOn;

        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        Console.WriteLine($"EasyAuth: Acquired access token (expires {tokenExpiry:u}).");
    }

    private async Task ForceRefreshTokenAsync()
    {
        Console.WriteLine("EasyAuth: Forcing token refresh...");
        accessToken = null;
        tokenExpiry = DateTimeOffset.MinValue;
        await EnsureAuthenticatedAsync();
    }

    public async Task KickOffScraping(string rootUrl, int maxDepth = 10)
    {
        Console.WriteLine($"KickOffScraping: Starting with root URL '{rootUrl}', max depth {maxDepth}.");
        Uri uri = new Uri(rootUrl);

        await EnsureAuthenticatedAsync();

        await ScrapeWebsiteAsync(rootUrl, maxDepth);
        Console.WriteLine($"KickOffScraping: Finished. Total URLs visited: {visitedUrls.Count}.");
    }

    private async Task ScrapeWebsiteAsync(string url, int maxDepth, int currentDepth = 0)
    {
        if (visitedUrls.Contains(url) || currentDepth > maxDepth)
            return;

        visitedUrls.Add(url);

        Console.WriteLine($"**********Scraping {url} at depth {currentDepth}**********");

        try
        {
            await EnsureAuthenticatedAsync();

            Console.WriteLine($"Scrape: GET {url}");
            var response = await httpClient.GetAsync(url);
            Console.WriteLine($"Scrape: Response {(int)response.StatusCode} {response.StatusCode} for {url}");

            if ((response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                 response.StatusCode == System.Net.HttpStatusCode.Forbidden) &&
                !string.IsNullOrEmpty(config.WEBSITE_EASYAGENT_EASYAUTH_AUDIENCE))
            {
                Console.WriteLine($"Scrape: Got {response.StatusCode}, forcing token refresh and retrying...");
                await ForceRefreshTokenAsync();
                response = await httpClient.GetAsync(url);
                Console.WriteLine($"Scrape: Retry response {(int)response.StatusCode} {response.StatusCode} for {url}");
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Redirect ||
                response.StatusCode == System.Net.HttpStatusCode.MovedPermanently)
            {
                var location = response.Headers.Location?.ToString() ?? "";
                Console.WriteLine($"Scrape: Redirect location: '{location}'");
                if (location.Contains("login.microsoftonline.com") || location.Contains("/.auth/login"))
                {
                    Console.WriteLine($"Scrape: EasyAuth redirect detected for {url} — token may be invalid.");
                    return;
                }

                // Follow legitimate redirects (e.g., http→https, www normalization)
                if (!string.IsNullOrEmpty(location))
                {
                    var absoluteRedirect = GetAbsoluteUrl(url, location);
                    Console.WriteLine($"Scrape: Following legitimate redirect to '{absoluteRedirect}'");
                    await ScrapeWebsiteAsync(absoluteRedirect, maxDepth, currentDepth);
                    return;
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Scrape: Failed to retrieve {url}: {response.StatusCode}");
                return;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"Scrape: Received {responseBody.Length} characters from {url}");
            var htmlDocument = new HtmlDocument();
            htmlDocument.LoadHtml(responseBody);

            await ExtractChunks(url, htmlDocument.DocumentNode);

            var linkNodes = ExtractLinks(htmlDocument.DocumentNode, url);
            Console.WriteLine($"Scrape: Found {linkNodes.Count()} links on {url}");

            foreach (var link in linkNodes)
            {
                await ScrapeWebsiteAsync(link, maxDepth, currentDepth + 1);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Scrape: Error scraping {url}: {ex.Message}");
            Console.WriteLine($"Scrape: Stack trace: {ex.StackTrace}");
        }
    }

    private IEnumerable<string> ExtractLinks(HtmlNode documentNode, string baseUrl)
    {
        var links = new List<string>();
        var baseHost = new Uri(baseUrl).Host;
        var linkNodes = documentNode.SelectNodes("//a[@href]");
        if (linkNodes != null)
        {
            foreach (var linkNode in linkNodes)
            {
                var href = linkNode.GetAttributeValue("href", string.Empty);
                if (!string.IsNullOrEmpty(href))
                {
                    var absoluteUrl = NormalizeUrl(GetAbsoluteUrl(baseUrl, href));
                    // Only follow links on the same host to avoid scraping external sites
                    if (Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var parsedUri)
                        && string.Equals(parsedUri.Host, baseHost, StringComparison.OrdinalIgnoreCase))
                    {
                        links.Add(absoluteUrl);
                    }
                }
            }
        }
        return links;
    }

    private static string NormalizeUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // Strip fragment and trailing slash
            var normalized = uri.GetLeftPart(UriPartial.Query).TrimEnd('/');
            return normalized;
        }
        return url;
    }

    private string GetAbsoluteUrl(string baseUrl, string relativeUrl)
    {
        if (Uri.TryCreate(relativeUrl, UriKind.Absolute, out var absoluteUri))
        {
            // If the URL is already absolute, return it as is  
            return absoluteUri.ToString();
        }

        // Otherwise, combine it with the base URL  
        var baseUri = new Uri(baseUrl);
        var combinedUri = new Uri(baseUri, relativeUrl);
        return combinedUri.ToString();
    }

    private async Task ExtractChunks(string url, HtmlNode node)
    {
        var ignoredTags = new HashSet<string> { "script", "style", "header", "footer", "nav" };
        var ignoredClassesAndIds = new HashSet<string> { "header", "footer", "nav", "toc", "table-of-contents" };

        if (ignoredTags.Contains(node.Name) ||
            (node.Attributes["class"] != null && ignoredClassesAndIds.Contains(node.Attributes["class"].Value.ToLower())) ||
            (node.Attributes["id"] != null && ignoredClassesAndIds.Contains(node.Attributes["id"].Value.ToLower())))
        {
            return;
        }

        var accumulatedText = new List<string>();

        async Task StoreAccumulatedChunksAsync()
        {
            if (accumulatedText.Count > 0)
            {
                var combinedText = string.Join(" ", accumulatedText);
                accumulatedText.Clear();

                if (combinedText.Length > 7000 * 4)
                {
                    Console.WriteLine("Given chunk is too long, breaking down.");
                    int dotIndex = combinedText.IndexOf('.', 5000);
                    int breakPoint = dotIndex >= 0 ? Math.Min(dotIndex + 1, 7000) : 7000;
                    await StoreChunk(url, combinedText[..breakPoint]);
                    await StoreChunk(url, combinedText[breakPoint..]);
                }
                else
                {
                    await StoreChunk(url, combinedText);
                }
            }
        }

        async Task ProcessNodeAsync(HtmlNode currentNode)
        {
            if (ignoredTags.Contains(currentNode.Name) ||
                (currentNode.Attributes["class"] != null && ignoredClassesAndIds.Contains(currentNode.Attributes["class"].Value.ToLower())) ||
                (currentNode.Attributes["id"] != null && ignoredClassesAndIds.Contains(currentNode.Attributes["id"].Value.ToLower())))
            {
                return;
            }

            if (currentNode.Name == "p" || currentNode.Name == "div")
            {
                // Only extract text from leaf-level content nodes (no nested p/div children)
                var hasNestedContentNodes = currentNode.ChildNodes.Any(c => c.Name == "p" || c.Name == "div");
                if (!hasNestedContentNodes)
                {
                    var text = HtmlEntity.DeEntitize(currentNode.InnerText.Trim());
                    if (!string.IsNullOrEmpty(text) && text.Any(char.IsLetterOrDigit))
                    {
                        var cleanedText = CleanWhitespace(text);
                        accumulatedText.Add(cleanedText);

                        var wordCount = accumulatedText.Sum(t => t.Split(' ').Length);
                        if (wordCount >= 200)
                        {
                            await StoreAccumulatedChunksAsync();
                        }
                    }
                    // Don't recurse — already captured via InnerText
                    return;
                }
            }

            foreach (var childNode in currentNode.ChildNodes)
            {
                await ProcessNodeAsync(childNode);
            }
        }

        await ProcessNodeAsync(node);

        // Final check to store any remaining accumulated text  
        await StoreAccumulatedChunksAsync();
    }

    private async Task StoreChunk(string url, string sentence)
    {
        var embedding = await GenerateEmbedding(sentence);

        var id = Guid.NewGuid().ToString();
        await db.AddEmbedding(new TextEmbeddingItem()
        {
            Id = id,
            Url = url,
            TextHash = ComputeHash(sentence),
            Text = sentence,
            Embedding = embedding
        });

        Console.WriteLine($"StoreChunk: {id} ({sentence.Length} chars) from {url}");
    }

    // Hash is used to check for duplicated text
    private static string ComputeHash(string text)
    {
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hashBytes);
    }

    private string CleanWhitespace(string input)
    {
        return Regex.Replace(input, @"\s+", " ").Trim();
    }

    private async Task<float[]> GenerateEmbedding(string sentence)
    {
        return await db.GenerateEmbedding(sentence);
    }
}
