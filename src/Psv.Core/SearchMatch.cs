namespace Psv.Core;

/// <summary>A match's position expressed as a character (not byte) column within a decoded line.</summary>
public readonly record struct SearchMatch(long LineNumber, int Column, int Length);
