namespace EQOverwatch.App.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// The first-run window's choices: anonymous sharing turns every sharing switch on or off except Server First, which has its own
    /// choice (its warning was on that window, so it is not asked again). The EverQuest folder, when one was picked, is then applied as
    /// if chosen on Settings.
    /// </summary>
    public async Task ApplyWelcomeAsync(string? eqFolder, bool anonymousSharing, bool serverFirst)
    {
        UseSharedDrops = anonymousSharing;
        ShareMyDrops = anonymousSharing;
        PoolNormalLootDrops = anonymousSharing;
        UseSharedLeveling = anonymousSharing;
        ShareMyLeveling = anonymousSharing;
        UseSharedMobStats = anonymousSharing;
        ShareMyMobStats = anonymousSharing;
        ShareResourceBonuses = anonymousSharing;
        CountMeIn = anonymousSharing;

        bool wasRestoring = _restoring;
        _restoring = true;
        ShareServerFirsts = serverFirst;
        _restoring = wasRestoring;
        UpdateShareSummaries();
        RequestSave();

        if (!string.IsNullOrWhiteSpace(eqFolder))
        {
            await ApplyEqFolderAsync(eqFolder);
        }
    }
}
