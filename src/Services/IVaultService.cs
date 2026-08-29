namespace KeySecBox;

public interface IVaultService : IDisposable
{
    ErrorCodes Setup(string basePath, string masterPassword);
    ErrorCodes Open(string basePath, string masterPassword);
    ErrorCodes ChangePassword(string newMasterPassword);
    ErrorCodes VerifyPassword(string masterPassword);

    long AddCategory(string name);
    ErrorCodes RenameCategory(long id, string name);
    ErrorCodes MoveCategory(long id, long newPos);
    ErrorCodes RemoveCategory(long id);
    List<Category> ListCategories();

    long AddEntry(IEnumerable<long> categoryIds, string account, string password, string note);
    ErrorCodes UpdateEntry(long id, IEnumerable<long> categoryIds, string account, string password, string note);
    ErrorCodes RemoveEntry(long id);
    ErrorCodes MoveEntry(long id, long categoryId, long newPos);
    ErrorCodes SetAllOrder(IEnumerable<long> orderedIds);
    EntryDetail? GetEntry(long id);

    ErrorCodes SetRecovery(long id, List<string> keys);
    List<string> GetRecovery(long id);

    List<EntrySummary> QueryAll();
    List<EntrySummary> QueryCategory(long categoryId);
    List<EntrySummary> Search(string keyword);

    ErrorCodes Save();

    bool GetDiagnostics();
    void SetDiagnostics(bool enabled);

    bool IsUnlocked { get; }
    bool IsLegacyMode { get; }
    bool IsDirty { get; }
}