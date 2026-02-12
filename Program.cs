using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Azure.Core;

class Program
{
    static async Task Main()
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
        Console.WriteLine($"Running in {environment} environment");

        // Build configuration with proper order: JSON files first, then environment variables override
        var config = new ConfigurationBuilder()
             .SetBasePath(Directory.GetCurrentDirectory())
             .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
             .AddEnvironmentVariables()
             .Build();

        // Bind configuration to strongly typed configuration class
        var chatbotConfig = new ChatbotConfiguration();
        config.Bind(chatbotConfig);

        // Display the configuration values to show they're working
        Console.WriteLine("Configuration loaded:");
        Console.WriteLine($"Foundry Endpoint: {chatbotConfig.WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT}");
        Console.WriteLine($"Chat Model: {chatbotConfig.WEBSITE_EASYAGENT_FOUNDRY_CHAT_MODEL}");
        Console.WriteLine($"Embedding Model: {chatbotConfig.WEBSITE_EASYAGENT_FOUNDRY_EMBEDDING_MODEL}");
        Console.WriteLine($"Database Endpoint: {chatbotConfig.WEBSITE_EASYAGENT_SITECONTEXT_DB_ENDPOINT}");
        Console.WriteLine($"Database Name: {chatbotConfig.WEBSITE_EASYAGENT_SITECONTEXT_DB_NAME}");
        Console.WriteLine($"Website Hostname: {chatbotConfig.WEBSITE_HOSTNAME}");
        Console.WriteLine($"Site Name: {chatbotConfig.WEBSITE_SITE_NAME}");
        Console.WriteLine($"EasyAuth Audience: {chatbotConfig.WEBSITE_EASYAGENT_EASYAUTH_AUDIENCE}");

        // Initialize Azure credentials based on configuration
        TokenCredential credential = !string.IsNullOrEmpty(chatbotConfig.WEBSITE_MANAGED_CLIENT_ID)
            ? new ManagedIdentityCredential(chatbotConfig.WEBSITE_MANAGED_CLIENT_ID)
            : new DefaultAzureCredential();
            
        Console.WriteLine(!string.IsNullOrEmpty(chatbotConfig.WEBSITE_MANAGED_CLIENT_ID)
            ? "Azure credentials initialized with Managed Identity."
            : "Azure credentials initialized with DefaultAzureCredential.");

        // Validate that required configuration is present
        if (string.IsNullOrEmpty(chatbotConfig.WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT))
        {
            Console.WriteLine("Warning: WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT is not configured");
        }

        if (string.IsNullOrEmpty(chatbotConfig.WEBSITE_EASYAGENT_SITECONTEXT_DB_ENDPOINT))
        {
            Console.WriteLine("Warning: WEBSITE_EASYAGENT_SITECONTEXT_DB_ENDPOINT is not configured");
        }

        if (string.IsNullOrEmpty(chatbotConfig.WEBSITE_EASYAGENT_EASYAUTH_AUDIENCE))
        {
            Console.WriteLine("Warning: WEBSITE_EASYAGENT_EASYAUTH_AUDIENCE is not configured — scraping EasyAuth-protected sites will fail");
        }

        try 
        {
            Console.WriteLine("Initializing Database Service...");
            var dbService = new DBService(chatbotConfig, credential);
            Console.WriteLine("Starting database setup...");
            await dbService.CreateDatabaseAndFreshContainerAsync();
            Console.WriteLine("Database setup complete.");

            Console.WriteLine("Initializing Website Scraping Service...");
            var scraper = new WebsiteScrapingService(chatbotConfig, dbService, credential);
            Console.WriteLine($"Starting scraping of {chatbotConfig.WEBSITE_HOSTNAME}...");
            await scraper.KickOffScraping("https://" + chatbotConfig.WEBSITE_HOSTNAME, 10);
            Console.WriteLine("Scraping complete.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during execution: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }

        Console.WriteLine("Configuration setup complete!");
    }
}