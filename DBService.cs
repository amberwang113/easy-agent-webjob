using System.Collections.ObjectModel;
using Azure.AI.Projects;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;

public class DBService
{
    private CosmosClient cosmosClient;
    private Container container;
    private string databaseId;
    private string containerId;
    private ChatbotConfiguration config;
    private DefaultAzureCredential credential;

    public class TextEmbeddingItem
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        // Url and partitionKey for the CosmosDb
        public string Url { get; set; }

        public string Text { get; set; }

        public float[] Embedding { get; set; }

        public string TextHash { get; set; }

        public override string ToString()
        {
            return $"Id: {Id}, PartitionKey (Url): {Url}, TextHash: {TextHash}, Text: {Text.Substring(0, Math.Min(Text.Length, 100))}]";
        }
    }

    public DBService(ChatbotConfiguration config, DefaultAzureCredential credential)
    {
        this.config = config;
        // Use the database name from config, or fall back to site name if not specified
        this.databaseId = string.IsNullOrEmpty(config.WEBSITE_EASYAGENT_SITECONTEXT_DB_NAME) 
            ? $"{config.WEBSITE_SITE_NAME}" 
            : config.WEBSITE_EASYAGENT_SITECONTEXT_DB_NAME;
        
        this.containerId = "base";
        this.cosmosClient = new CosmosClient(config.WEBSITE_EASYAGENT_SITECONTEXT_DB_ENDPOINT, credential);
        this.credential = credential;
    }

    public async Task CreateDatabaseAndFreshContainerAsync()
    {
        var database = await this.cosmosClient.CreateDatabaseIfNotExistsAsync(this.databaseId);

        var container = database.Database.GetContainer(this.containerId);

        // Delete the old container
        try
        {
            await container.DeleteContainerAsync();
        }
        catch (Exception e)
        {
            if (e.Message.Contains("NotFound"))
            {
                Console.WriteLine($"No existing container. Creating a new one.");
            }
        }

        // Create a new empty container with vector index
        List<Embedding> embeddings = new List<Embedding>()
        {
            new Embedding()
            {
                Path = "/Embedding",
                DataType = VectorDataType.Float32,
                DistanceFunction = DistanceFunction.Cosine,
                Dimensions = 1536,
            }
        };

        Collection<Embedding> collection = new Collection<Embedding>(embeddings);
        ContainerProperties properties = new ContainerProperties(id: this.containerId, partitionKeyPath: "/Url")
        {
            VectorEmbeddingPolicy = new(collection),
            IndexingPolicy = new IndexingPolicy()
            {
                VectorIndexes =
                [
                    new VectorIndexPath()
                    {
                        Path = "/Embedding",
                        Type = VectorIndexType.DiskANN,
                    }
                ]
            },
        };
        properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
        properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/Embedding/*" });

        this.container = await database.Database.CreateContainerIfNotExistsAsync(properties);
    }

    public async Task AddEmbedding(TextEmbeddingItem item)
    {
        try
        {
            if (!await IsDuplicateTextAsync(item))
            {
                await container.UpsertItemAsync(item, new PartitionKey(item.Url));
            }
            else
            {
                Console.WriteLine($"Duplicate item found: {item.ToString()}. Did not insert.");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"Error in the Cosmos Upsert: {e}");
            throw;
        }
    }

    private async Task<bool> IsDuplicateTextAsync(TextEmbeddingItem item)
    {
        var queryDefinition = new QueryDefinition("SELECT * FROM c WHERE c.TextHash = @textHash AND c.Url = @url")
        .WithParameter("@textHash", item.TextHash)
        .WithParameter("@url", item.Url);
        var queryIterator = container.GetItemQueryIterator<TextEmbeddingItem>(queryDefinition, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(item.Url) });

        while (queryIterator.HasMoreResults)
        {
            var response = await queryIterator.ReadNextAsync();
            if (response.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    public async Task<float[]> GenerateEmbedding(string sentence)
    {
        var aClient = new AIProjectClient(new Uri(config.WEBSITE_EASYAGENT_FOUNDRY_ENDPOINT), credential);

        var eClient = aClient.GetAzureOpenAIEmbeddingClient(deploymentName: config.WEBSITE_EASYAGENT_FOUNDRY_EMBEDDING_MODEL);

        var embedding = eClient.GenerateEmbedding(sentence);

        return embedding.Value.ToFloats().ToArray();
    }
}