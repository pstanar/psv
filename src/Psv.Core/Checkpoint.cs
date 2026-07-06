namespace Psv.Core;

public readonly record struct Checkpoint(long LineNumber, long ByteOffset);
