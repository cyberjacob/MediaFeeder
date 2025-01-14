using MassTransit;
using MediaFeeder.Data;
using MediaFeeder.Tasks;
using Microsoft.EntityFrameworkCore;
using System.ServiceModel.Syndication;
using System.Xml;

namespace MediaFeeder.Providers.RSS;

public class RSSSubscriptionSynchroniseConsumer(
    ILogger<RSSSubscriptionSynchroniseConsumer> logger,
    IDbContextFactory<MediaFeederDataContext> contextFactory,
    IHttpClientFactory httpClientFactory
) : IConsumer<SynchroniseSubscriptionContract<RSSProvider>>
{
    public async Task Consume(ConsumeContext<SynchroniseSubscriptionContract<RSSProvider>> context)
    {
        await using var db = await contextFactory.CreateDbContextAsync(context.CancellationToken);

        var subscription =
            await db.Subscriptions.SingleAsync(s => s.Id == context.Message.SubscriptionId, context.CancellationToken);
        logger.LogInformation("Starting synchronize {}", subscription.Name);

        foreach (var video in await db.Videos
                     .Where(v => v.SubscriptionId == subscription.Id && v.New && DateTimeOffset.UtcNow - v.PublishDate <= TimeSpan.FromDays(1))
                     .ToListAsync(context.CancellationToken)
                )
            video.New = false;

        await db.SaveChangesAsync(context.CancellationToken);

        logger.LogInformation("Starting check new videos {}", subscription.Name);

        using var client = httpClientFactory.CreateClient("retry");
        var feedResponse = await client.GetAsync(subscription.ChannelId, context.CancellationToken);
        feedResponse.EnsureSuccessStatusCode();

        var feedReader = XmlReader.Create(await feedResponse.Content.ReadAsStreamAsync(context.CancellationToken));
        var feed = SyndicationFeed.Load(feedReader);

        if (subscription.Name == subscription.ChannelName)
            subscription.Name = feed.Title.Text;
        subscription.ChannelName = feed.Title.Text;

        // Need to make absolute
        subscription.Thumb = new Uri(feed.Links.First().Uri, feed.ImageUrl).AbsoluteUri;
        subscription.Thumbnail = new Uri(feed.Links.First().Uri, feed.ImageUrl).AbsoluteUri;

        await db.SaveChangesAsync(context.CancellationToken);

        foreach (var item in feed.Items)
            await SyncVideo(item, subscription.Id, context.CancellationToken);
    }

    private async Task SyncVideo(SyndicationItem item, int subscriptionId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var video = await db.Videos.SingleOrDefaultAsync(v => v.VideoId == item.Id && v.SubscriptionId == subscriptionId, cancellationToken) ?? db.Videos.CreateProxy();

        video.VideoId = item.ElementExtensions.ReadElementExtensions<string>("identifier", "http://purl.org/dc/elements/1.1/").FirstOrDefault() ?? item.Id;
        video.Name = item.Title.Text;
        video.New = DateTimeOffset.UtcNow - item.PublishDate <= TimeSpan.FromDays(7);
        video.PublishDate = item.PublishDate;
        //video.Thumb = "";
        video.Description = item.Summary.Text;
        var rawDuration = item.ElementExtensions
            .ReadElementExtensions<string>("duration", "http://www.itunes.com/dtds/podcast-1.0.dtd")
            .FirstOrDefault();
        if (rawDuration != null)
            video.DurationSpan = TimeSpan.Parse(rawDuration);
        video.SubscriptionId = subscriptionId;
        video.UploaderName = string.Join(", ", item.ElementExtensions.ReadElementExtensions<string>("author", "http://www.itunes.com/dtds/podcast-1.0.dtd")
                                               ?? item.Authors.Select(static a => a.Name));
        video.DownloadedPath = item.Links.SingleOrDefault(static l => l.RelationshipType == "enclosure")?.Uri.ToString();

        await db.SaveChangesAsync(cancellationToken);
    }
}
