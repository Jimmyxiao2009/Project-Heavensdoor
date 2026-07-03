namespace QQReborn.App.Models
{
    /// <summary>
    /// Navigation payload for the (fake) audio/video call pages.
    /// Passed as the parameter of Frame.Navigate(typeof(VoiceCallPage)/VideoCallPage, ...).
    /// Plain fields are intentional: this is a lightweight DTO, not an observable model.
    /// </summary>
    public class CallArgs
    {
        public string PeerName;
        public string PeerAvatar;
        public bool IsVideo;
    }
}
