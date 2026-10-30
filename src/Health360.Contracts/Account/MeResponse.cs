namespace Health360.Contracts.Account;

/// <summary>
/// <c>GET /me</c>: who the API thinks the caller is. <see cref="UserId"/> is read back from the
/// database session (<c>auth.uid()</c>), and the lists come through RLS, so they show exactly what a
/// query made on the caller's behalf can see.
/// </summary>
public sealed record MeResponse(Guid UserId, IReadOnlyList<ProfileSummary> Profiles, IReadOnlyList<Guid> HouseholdIds);

/// <summary>A profile the caller owns or manages.</summary>
public sealed record ProfileSummary(Guid Id, string DisplayName);
