namespace KeySecBox;

public interface IRecoveryService
{
    ErrorCodes SetKeys(long entryId, List<string> keys);
    List<string> GetKeys(long entryId);
}

public class RecoveryService : IRecoveryService
{
    private readonly IVaultService _vault;

    public RecoveryService(IVaultService vault)
    {
        _vault = vault;
    }

    public ErrorCodes SetKeys(long entryId, List<string> keys)
    {
        return _vault.SetRecovery(entryId, keys);
    }

    public List<string> GetKeys(long entryId)
    {
        return _vault.GetRecovery(entryId);
    }
}