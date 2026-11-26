using FieldService.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace FieldService.Api.Data;

/// <summary>Katalog danych na dysku (klucz podpisu JWT).</summary>
public sealed class StoragePaths
{
    public StoragePaths(IOptions<StorageOptions> options, IHostEnvironment env)
    {
        var dir = options.Value.DataDirectory;
        DataDirectory = Path.GetFullPath(Path.IsPathRooted(dir) ? dir : Path.Combine(env.ContentRootPath, dir));
        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory { get; }
}
