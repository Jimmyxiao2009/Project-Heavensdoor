using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using QQReborn.App.Models;

namespace QQReborn.App.Views
{
    /// <summary>Picks the left/right bubble template (or the centered system line) for a message.</summary>
    public class MessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate IncomingTemplate { get; set; }
        public DataTemplate OutgoingTemplate { get; set; }
        public DataTemplate SystemTemplate { get; set; }
        public DataTemplate IncomingVideoTemplate { get; set; }
        public DataTemplate OutgoingVideoTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            return Pick(item);
        }

        protected override DataTemplate SelectTemplateCore(object item)
        {
            return Pick(item);
        }

        private DataTemplate Pick(object item)
        {
            if (item is ChatMessage m)
            {
                if (m.IsSystem && SystemTemplate != null) return SystemTemplate;
                if (m.IsVideo)
                {
                    if (m.IsOutgoing && OutgoingVideoTemplate != null) return OutgoingVideoTemplate;
                    if (!m.IsOutgoing && IncomingVideoTemplate != null) return IncomingVideoTemplate;
                }
                if (m.IsOutgoing) return OutgoingTemplate;
            }
            return IncomingTemplate;
        }
    }
}
