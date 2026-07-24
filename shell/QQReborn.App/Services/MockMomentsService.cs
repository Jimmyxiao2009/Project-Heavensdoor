using System.Collections.Generic;
using System.Threading.Tasks;
using QQReborn.App.Models;

namespace QQReborn.App.Services
{
    /// <summary>
    /// In-memory fake friend-feed: a hand-authored set of rich, realistic Chinese
    /// moments (text-only, single photo, 3-up photos, with likes &amp; comments).
    /// Photos reuse bundled logos / classic QQ emoticons as stand-in images.
    /// </summary>
    public class MockMomentsService : IMomentsService
    {
        private const string FriendAvatar = "ms-appx:///Assets/Avatars/DefaultUserAvatar.png";
        private const string GroupAvatar = "ms-appx:///Assets/Avatars/DefaultTroopAvatar.png";

        // Stand-in "photos" pulled from bundled assets.
        private const string Pic1 = "ms-appx:///Assets/Square310x310Logo.scale-200.png";
        private const string Pic2 = "ms-appx:///Assets/Wide310x150Logo.scale-200.png";
        private const string Pic3 = "ms-appx:///Assets/Square150x150Logo.scale-200.png";
        private const string Face1 = "ms-appx:///Assets/Emoticons/dangao.png";
        private const string Face2 = "ms-appx:///Assets/Emoticons/aixin.png";
        private const string Face3 = "ms-appx:///Assets/Emoticons/woshou.png";
        private const string Face4 = "ms-appx:///Assets/Emoticons/taiyang.png";
        private const string Face5 = "ms-appx:///Assets/Emoticons/qiang.png";

        private const string SelfName = "Jimmy";

        private List<Moment> _feed;
        private int _idSeed;

        private string NextId() => "mo" + (++_idSeed);

        private List<Moment> BuildFeed()
        {
            var list = new List<Moment>
            {
                Make("张三", FriendAvatar, "刚到公司，今天又是元气满满的一天！加油打工人💪", "5分钟前", null,
                    8, false,
                    C("李四", "打工人！打工魂！"),
                    C("王五", "早啊"),
                    C("前端老哥", "摸鱼时间到")),

                Make("李四", FriendAvatar, "新到手的 Lumia 950 XL，情怀拉满，WP 永不为奴！", "20分钟前",
                    new List<string> { Pic1 },
                    23, true,
                    C("张三", "钉子户实锤了"),
                    C("Jimmy", "终于换机了哈哈"),
                    C("王五", "1020 路过")),

                Make("老妈", FriendAvatar, "今天做了红烧排骨和糖醋里脊，回家吃饭记得早点回来。", "1小时前",
                    new List<string> { Pic2, Pic3, Pic1 },
                    15, false,
                    C("Jimmy", "妈我今晚加班，给我留一份！"),
                    C("妹妹", "我要吃排骨🍖")),

                Make("前端老哥", FriendAvatar, "调了一下午的 CSS 居中，最后发现是没写 box-sizing。CSS 真是门玄学。", "3小时前",
                    null,
                    42, true,
                    C("张三", "哈哈哈哈经典"),
                    C("李四", "flex 走起"),
                    C("王五", "margin: auto 永远的神")),

                Make("WP 钉子户交流群", GroupAvatar, "周末线下聚会安排！老地方，记得带上你的老 WP 手机来合影📷", "昨天 21:30",
                    new List<string> { Face1, Face2, Face3 },
                    37, false,
                    C("李四", "950 XL 准时到"),
                    C("张三", "我带 1520"),
                    C("Jimmy", "+1"),
                    C("王五", "求合影")),

                Make("王五", FriendAvatar, "终于把毕业论文交了，撒花🎉🎉🎉", "昨天 18:05",
                    new List<string> { Pic3 },
                    56, true,
                    C("前端老哥", "恭喜毕业！"),
                    C("张三", "牛啊"),
                    C("老妈", "出息了")),

                Make("老板", FriendAvatar, "公司年会定在下个月，今年大家都辛苦了，准备了丰厚的奖品🎁", "2天前",
                    null,
                    19, false,
                    C("Jimmy", "期待奖品！"),
                    C("前端老哥", "老板大气")),

                Make("妹妹", FriendAvatar, "今天的日落也太好看了吧，随手一拍就是壁纸级别✨", "3天前",
                    new List<string> { Face4, Face5 },
                    64, true,
                    C("老妈", "我闺女拍得真好"),
                    C("Jimmy", "确实绝"),
                    C("张三", "求原图"))
            };
            return list;
        }

        private Moment Make(string author, string avatar, string text, string time,
            List<string> images, int likes, bool liked, params MomentComment[] comments)
        {
            var m = new Moment
            {
                Id = NextId(),
                AuthorName = author,
                AuthorAvatarPath = avatar,
                Text = text,
                TimeText = time,
                ImagePaths = images ?? new List<string>(),
                LikeCount = likes,
                IsLiked = liked
            };
            if (comments != null)
            {
                foreach (var c in comments) m.Comments.Add(c);
            }
            return m;
        }

        private static MomentComment C(string author, string text)
            => new MomentComment { Author = author, Text = text };

        public Task<IReadOnlyList<Moment>> GetFeedAsync()
        {
            if (_feed == null) _feed = BuildFeed();
            IReadOnlyList<Moment> result = _feed;
            return Task.FromResult(result);
        }

        public Task ToggleLikeAsync(Moment m)
        {
            if (m != null)
            {
                if (m.IsLiked)
                {
                    m.IsLiked = false;
                    m.LikeCount = m.LikeCount > 0 ? m.LikeCount - 1 : 0;
                }
                else
                {
                    m.IsLiked = true;
                    m.LikeCount = m.LikeCount + 1;
                }
            }
            return Task.CompletedTask;
        }

        public Task AddCommentAsync(Moment m, string text)
        {
            if (m != null && !string.IsNullOrWhiteSpace(text))
            {
                m.Comments.Add(new MomentComment { Author = SelfName, Text = text.Trim() });
                m.RaiseCommentsChanged();
            }
            return Task.CompletedTask;
        }

        public Task<bool> GetEarlierFeedAsync() => Task.FromResult(false);
    }
}
