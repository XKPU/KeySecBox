namespace KeySecBox;

public partial class VaultService : IVaultService
{
    private readonly ICryptoService _crypto;
    private readonly IFileIOService _fileIO;
    private readonly IBinaryFormatService _binary;
    private readonly IJsonSerializationService _json;
    private readonly IDiagnosticService _diag;

    internal readonly VaultStore S = new();

    public bool IsUnlocked => S.Unlocked;
    public bool IsLegacyMode => S.LegacyMode;
    public bool IsDirty => S.IsDirty;

    public VaultService(
        ICryptoService crypto,
        IFileIOService fileIO,
        IBinaryFormatService binary,
        IJsonSerializationService json,
        IDiagnosticService diag)
    {
        _crypto = crypto;
        _fileIO = fileIO;
        _binary = binary;
        _json = json;
        _diag = diag;
    }

    public void Dispose()
    {
        S.Dispose();
    }

    private string CatPath => S.BasePath + ".cats";
    private string MapPath => S.BasePath + ".map";
    private string EntriesPath => S.BasePath + ".entries";
    private string RecoveryPath => S.BasePath + ".recovery";
    private string MasterPath => S.BasePath + ".master";
    private string PrefsPath => S.BasePath + ".prefs";

    private void Diag(string fmt, params object?[] args)
    {
        if (S.Diag)
            _diag.Log(fmt, args);
    }
}