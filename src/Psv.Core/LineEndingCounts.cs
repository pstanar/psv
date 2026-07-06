namespace Psv.Core;

internal struct LineEndingCounts
{
    public long Lf;
    public long Cr;
    public long CrLf;

    public void Record(LineEndingKind kind)
    {
        switch (kind)
        {
            case LineEndingKind.Lf:
                Lf++;
                break;
            case LineEndingKind.Cr:
                Cr++;
                break;
            case LineEndingKind.CrLf:
                CrLf++;
                break;
        }
    }

    public readonly LineEndingKind? Dominant()
    {
        if (Lf == 0 && Cr == 0 && CrLf == 0)
        {
            return null;
        }

        if (Lf >= Cr && Lf >= CrLf)
        {
            return LineEndingKind.Lf;
        }

        return CrLf >= Cr ? LineEndingKind.CrLf : LineEndingKind.Cr;
    }
}
