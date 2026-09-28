namespace BeeMemoryBank.Web.Models;

/// <summary>Reply from GET /api/recovery/status: the blind-node recovery boxes as this node sees them.</summary>
public record RecoveryStatusDto(
    int ActiveBoxes,
    int StrongBoxes,
    int DeviceBoxes,
    string? CurrentKeyFingerprint,
    bool CurrentKeyCovered,
    bool CurrentKeyHasStrongBox,
    List<string> DevicesWithOtherPassword);

/// <summary>Reply from GET /api/restore/progress (restore of a fresh node from a blind node or its backup).</summary>
public record BlindRestoreProgressDto(string State, string? Error, RestoreAnchorDto? Anchor, int? RetiredKeys, int? RemainingBoxes = null,
    string? Claim = null, List<RestorePeerRefDto>? UnconfirmedPeers = null);

public record RestorePeerRefDto(Guid Id, string Name, bool WasSuperadmin);

/// <summary>A device a restore kept inactive, as GET /api/recovery/restored-peers lists it.</summary>
public record RestoredPeerDto(Guid NodeId, string DisplayName, string? ApiAddress);

public record RestoreAnchorDto(string State, bool Found, bool MatchesAnchor, bool Confirmed, string? CreatedAt,
    int NewerRows, int NewerRowsSigned, int UncheckedRows, List<RestoreUnverifiedDto> Unverified, List<string> UnconfirmedAnchorDates,
    string? AsOf = null, bool HeadProven = true, string History = "unchecked", int HistoryUnverified = 0,
    List<RestoreUnverifiedDto>? Unchecked = null, int HistoryDifferingGroups = 0, List<RestoreUnverifiedDto>? HistoryNotVouched = null);

public record RestoreUnverifiedDto(string Type, string Id);
