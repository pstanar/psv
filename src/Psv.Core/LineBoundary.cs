namespace Psv.Core;

public readonly record struct LineBoundary(long Offset, int TerminatorLength, LineEndingKind Kind);
