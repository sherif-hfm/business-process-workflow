using System.Text.Json;

namespace Flowbit.Service.Models;

/// <summary>
/// Canonical, database-executable representation of one authored user-task
/// inbox visibility condition.
/// </summary>
/// <param name="ProgramVersion">Version of the postfix program contract.</param>
/// <param name="Program">Self-contained canonical JSON postfix program.</param>
/// <param name="VariableNames">
/// Canonically spelled instance-variable names needed by the program.
/// </param>
/// <param name="ExternalReferences">
/// Canonical lower-case sys.*, config.*, and setting.* names needed by the program.
/// </param>
/// <param name="SemanticFingerprint">
/// Lower-case SHA-256 of the canonical UTF-8 program JSON.
/// </param>
public sealed record InboxVisibilityConditionCompilation(
    int ProgramVersion,
    JsonElement Program,
    IReadOnlyList<string> VariableNames,
    IReadOnlyList<string> ExternalReferences,
    string SemanticFingerprint);

