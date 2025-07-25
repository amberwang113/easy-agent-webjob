using HtmlAgilityPack;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using static DBService;
using Azure.AI.Projects;
using Azure.Identity;

public class WebsiteScrapingService
{
    private HttpClient httpClient;
    private HashSet<string> visitedUrls = new HashSet<string>();

    private DBService db;

    private ChatbotConfiguration config;

    private DefaultAzureCredential credential;

    public WebsiteScrapingService(ChatbotConfiguration config, DBService db, DefaultAzureCredential credential)
    {
        this.httpClient = new HttpClient();
        this.db = db;
        this.config = config;
        this.credential = credential;
    }

    public async Task KickOffScraping(string rootUrl, int maxDepth = 10)
    {
        Uri uri = new Uri(rootUrl);

        await ScrapeWebsiteAsync(rootUrl, maxDepth);
    }

    private async Task ScrapeWebsiteAsync(string url, int maxDepth, int currentDepth = 0)
    {
        if (visitedUrls.Contains(url) || currentDepth > maxDepth)
            return;

        visitedUrls.Add(url);

        Console.WriteLine($"**********Scraping {url} at depth {currentDepth}**********");

        try
        {
            var response = await httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Failed to retrieve {url}");
                return;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            var htmlDocument = new HtmlDocument();
            htmlDocument.LoadHtml(responseBody);

            await ExtractChunks(url, htmlDocument.DocumentNode);

            var linkNodes = ExtractLinks(htmlDocument.DocumentNode, url); ;

            foreach (var link in linkNodes)
            {
                await ScrapeWebsiteAsync(link, maxDepth, currentDepth + 1);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error scraping {url}: {ex.Message}");
        }
        finally
        {
        }
    }

    private IEnumerable<string> ExtractLinks(HtmlNode documentNode, string baseUrl)
    {
        var links = new List<string>();
        var linkNodes = documentNode.SelectNodes("//a[@href]");
        if (linkNodes != null)
        {
            foreach (var linkNode in linkNodes)
            {
                var href = linkNode.GetAttributeValue("href", string.Empty);
                if (!string.IsNullOrEmpty(href))
                {
                    var absoluteUrl = GetAbsoluteUrl(baseUrl, href);
                    links.Add(absoluteUrl);
                }
            }
        }
        return links;
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
                    Console.WriteLine($"Given chunk is too long, breaking down.");
                    int breakPoint = Math.Min(combinedText.IndexOf('.', 5000), 7000);
                    await StoreChunk(url, combinedText.Substring(0, breakPoint));
                    await StoreChunk(url, combinedText.Substring(breakPoint));
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

        await db.AddEmbedding(new TextEmbeddingItem()
        {
            Id = Guid.NewGuid().ToString(),
            Url = url,
            TextHash = ComputeHash(sentence),
            Text = sentence,
            Embedding = embedding
        });

        Console.WriteLine($"Stored sentence: {sentence}.");
    }

    // Hash is used to check for duplicated text
    private string ComputeHash(string text)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(text));
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLower();
        }
    }

    private string CleanWhitespace(string input)
    {
        return Regex.Replace(input, @"\s+", " ").Trim();
    }

    public async Task<float[]> GenerateEmbedding(string sentence)
    {
        var aClient = new AIProjectClient(new Uri(config.WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT), credential);

        var eClient = aClient.GetAzureOpenAIEmbeddingClient(deploymentName: config.WEBSITE_EASYAGENT_FOUNDRY_EMBEDDING_MODEL);

        var embedding = eClient.GenerateEmbedding(sentence);

        return embedding.Value.ToFloats().ToArray();
    }
}
