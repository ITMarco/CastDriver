using Windows.Media.Control;

namespace CastDriver.UI;

// Reads the system's current media (track title + artist) via Windows' SMTC, the same
// info shown on the media keys / volume flyout. Best-effort: returns null if nothing is
// playing or the API is unavailable.
public static class NowPlaying
{
    public static async Task<string?> GetTitleAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = manager.GetCurrentSession();
            if (session == null) return null;

            var props  = await session.TryGetMediaPropertiesAsync();
            var title  = props?.Title;
            var artist = props?.Artist;
            if (string.IsNullOrWhiteSpace(title)) return null;

            return string.IsNullOrWhiteSpace(artist) ? title.Trim() : $"{artist.Trim()} — {title.Trim()}";
        }
        catch { return null; }
    }
}
