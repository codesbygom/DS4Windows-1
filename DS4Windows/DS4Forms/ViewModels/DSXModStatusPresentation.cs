namespace DS4WinWPF.DS4Forms.ViewModels
{
    internal static class DSXModStatusPresentation
    {
        internal static string Describe(bool requested, bool serviceRunning, bool listening,
            string address, int port, string error, bool changing = false)
        {
            if (changing) return "Applying game mod settings…";
            if (!string.IsNullOrWhiteSpace(error)) return error;
            if (!requested) return "Off — game mods cannot change your controller.";
            if (!serviceRunning) return "Enabled — press Start in DS4Windows to accept game mods.";
            if (!listening) return "Not listening — open Connection details and choose Apply / Retry.";
            string endpoint = address?.Contains(':') == true ? $"[{address}]:{port}" : $"{address}:{port}";
            return $"Listening on {endpoint}. Connect a DualSense or DualSense Edge for mod effects.";
        }
    }
}
