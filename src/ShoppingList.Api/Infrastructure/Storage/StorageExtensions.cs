using Minio;
using ShoppingList.Api.Configuration;

namespace ShoppingList.Api.Infrastructure.Storage;

public static class StorageExtensions
{
    public static IServiceCollection AddObjectStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSettings<MinioSettings>(MinioSettings.SectionName);

        services.AddSingleton<IMinioClient>(_ => new MinioClient()
            .WithEndpoint(settings.Endpoint)
            .WithCredentials(settings.AccessKey, settings.SecretKey)
            .WithSSL(settings.UseSsl)
            .Build());

        services.AddSingleton<IObjectStorage, MinioObjectStorage>();
        services.AddHostedService<ObjectStorageInitializer>();

        return services;
    }
}
