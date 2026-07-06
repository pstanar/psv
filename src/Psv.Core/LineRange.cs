namespace Psv.Core;

public readonly record struct LineRange(long StartOffset, int ContentLength, int TerminatorLength);
