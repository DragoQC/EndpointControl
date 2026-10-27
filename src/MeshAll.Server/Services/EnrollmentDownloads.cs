namespace MeshAll.Server.Services;

/// <summary>All native formats are derived from the selected enrollment's single credential.</summary>
public sealed class EnrollmentDownloads(Store store, NativeAgentHub agents, NativeMsiBuilder builder, AgentInstallerDownloads legacyInstallers)
{
    private readonly SemaphoreSlim recovery = new(1);

    public async Task<EnrollmentKeyDetails> DetailsAsync(UserInfo actor, string id, CancellationToken cancellationToken)
    {
        EnrollmentKeyDetails details = store.ReadEnrollmentKey(actor, id);
        if (details.NativeKey is not null || !store.EnrollmentTokenUsable(id))
        {
            return details;
        }
        await recovery.WaitAsync(cancellationToken);
        try
        {
            details = store.ReadEnrollmentKey(actor, id);
            AgentInstallerInfo? saved = legacyInstallers.Current;
            if (details.NativeKey is not null || saved?.TokenId != id)
            {
                return details;
            }
            byte[] executable = await builder.ExtractAgentAsync(await legacyInstallers.ReadAsync(saved, cancellationToken), cancellationToken);
            if (NativeMsiPackage.ServerIdentity(executable) != agents.ServerId)
            {
                throw new InvalidOperationException("The installer does not match this enrollment key.");
            }
            string[] nativeKeys = NativeAgentExecutable.EmbeddedPolicy(executable).Split('\n')
                .Where(line => line.StartsWith("MeshID=0x", StringComparison.Ordinal)).ToArray();
            if (nativeKeys.Length != 1)
            {
                throw new InvalidOperationException("The installer does not match this enrollment key.");
            }
            store.RecoverNativeEnrollmentKey(actor, id, nativeKeys[0][9..].Trim().ToLowerInvariant());
            store.Audit(actor.Name, "Enrollment native credential recovered", details.Information.Name);
            return store.ReadEnrollmentKey(actor, id);
        }
        finally
        {
            recovery.Release();
        }
    }

    public async Task<byte[]> ExecutableAsync(UserInfo actor, string id, string platform, CancellationToken cancellationToken)
    {
        EnrollmentKeyDetails details = await DetailsAsync(actor, id, cancellationToken);
        return agents.EnrollmentExecutable(details, platform);
    }

    public async Task<byte[]> MsiAsync(UserInfo actor, string id, string platform, CancellationToken cancellationToken)
    {
        if (platform != NativeMsiPackage.Platform)
        {
            throw new InvalidOperationException("MSI packaging currently supports Windows x64.");
        }
        byte[] executable = await ExecutableAsync(actor, id, platform, cancellationToken);
        byte[] package = await builder.BuildAsync(executable, agents.LicenseNotices(), cancellationToken);
        store.ReadEnrollmentKey(actor, id); // Recheck current permission after compilation.
        if (!store.EnrollmentTokenUsable(id))
        {
            throw new InvalidOperationException("Enrollment key is invalid, expired, or exhausted.");
        }
        return package;
    }
}
