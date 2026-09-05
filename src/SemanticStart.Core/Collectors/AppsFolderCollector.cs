using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Collectors;

public sealed class AppsFolderCollector : IEntityCollector
{
    public string Source => "appsfolder";

    public bool IsSupported => OperatingSystem.IsWindows() && Type.GetTypeFromProgID("Shell.Application") is not null;

    public async IAsyncEnumerable<Entity> CollectAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();

        if (!IsSupported)
            yield break;

        object? shell = null;
        object? folder = null;
        object? items = null;

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
                yield break;

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
                yield break;

            folder = shellType.InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, ["shell:AppsFolder"]);
            if (folder is null)
                yield break;

            items = folder.GetType().InvokeMember("Items", System.Reflection.BindingFlags.InvokeMethod, null, folder, null);
        }
        catch (COMException)
        {
            yield break;
        }
        catch (InvalidOperationException)
        {
            yield break;
        }

        if (items is not IEnumerable enumerable)
            yield break;

        foreach (var item in enumerable)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Entity? entity = TryCreateEntity(item);
            if (entity is not null)
                yield return entity;
        }
    }

    private Entity? TryCreateEntity(object item)
    {
        try
        {
            var type = item.GetType();
            var displayName = Convert.ToString(type.InvokeMember("Name", System.Reflection.BindingFlags.GetProperty, null, item, null));
            var aumid = Convert.ToString(type.InvokeMember("ExtendedProperty", System.Reflection.BindingFlags.InvokeMethod, null, item, ["System.AppUserModel.ID"])) ?? Convert.ToString(type.InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, item, null));

            if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(aumid))
                return null;

            if (aumid.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
                aumid = aumid[17..];

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["appUserModelId"] = aumid,
            };

            var entity = new Entity
            {
                Id = EntityId.Create(Source, aumid),
                Kind = aumid.Contains('!', StringComparison.Ordinal) ? EntityKind.PackagedApp : EntityKind.Application,
                DisplayName = displayName,
                LaunchKind = LaunchKind.AppsFolder,
                LaunchTarget = aumid,
                Source = Source,
                RawMetadata = metadata,
            };

            return CollectorEntity.WithContentHash(entity);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}

