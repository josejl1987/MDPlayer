namespace Fmp.Core.Rendering;

/// <summary>
/// Shared PPZ8 command representation, used both on the legacy path's capture
/// tap and on the replay path. Mirrors the shared MDSound PPZ8 renderer's
/// port-write command (<c>port</c>, <c>address</c>, <c>data</c>). A bank-load
/// command additionally carries a capture-local <see cref="Ppz8BankSnapshot"/>
/// reference (<c>BankId</c>) so replay never reopens the source file.
/// </summary>
internal readonly record struct Ppz8Command(int Port, int Address, int Data, int BankId = -1);
