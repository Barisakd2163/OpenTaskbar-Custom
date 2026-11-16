namespace OpenTaskbar.Services;

public readonly record struct DockLayoutMetrics(int ShelfPixels, int HeadroomPixels)
{
    public int WindowPixels => ShelfPixels + HeadroomPixels;

    public static DockLayoutMetrics Calculate(double minimumShelfHeight, double nativeShelfHeight, double headroom, uint dpi)
    {
        ArgumentOutOfRangeException.ThrowIfZero(dpi);
        return new DockLayoutMetrics(
            checked((int)Math.Ceiling(Math.Max(minimumShelfHeight, nativeShelfHeight) * dpi / 96d)),
            checked((int)Math.Ceiling(headroom * dpi / 96d)));
    }

    public int AdditionalReservation(int existingBottomReservation, bool autoHide) =>
        autoHide ? 0 : Math.Max(0, ShelfPixels - existingBottomReservation);
}
