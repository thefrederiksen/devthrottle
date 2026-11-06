using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Pins the microphone-quality verdict, which is the whole point of the background monitoring: a
/// number nobody acts on is worthless, and a warning about a microphone that is fine is worse than
/// worthless because it teaches the user to ignore the screen.
///
/// The calibration behind these thresholds is not a guess. The check was run over 212 REAL dictations
/// from a healthy setup: warning on every imperfection would have complained about roughly one
/// dictation in nine, while warning only on the two unambiguous defects would have said nothing at
/// all. These tests encode that: silence on a good setup, and a clear accusation on a bad one.
/// </summary>
public sealed class MicrophoneQualityFoldTests
{
    /// <summary>The fixed "now" every sample is placed back from. The fold reads no clock - it groups by the calendar
    /// day of each sample's own time - so the test must not either: built from the real clock, a sample "1 minute ago"
    /// run just after midnight UTC falls on yesterday, and a test that counts days then counts one too many.</summary>
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    private static MicrophoneQualityRecord Sample(
        string device = "Good Mic",
        bool narrowband = false,
        double clipped = 0,
        double speechDb = -20,
        double snrDb = 45,
        int minutesAgo = 0,
        string deviceId = "",
        string platform = "")
        => new()
        {
            TimestampUtc = Now.AddMinutes(-minutesAgo),
            Device = device,
            DeviceId = deviceId,
            Platform = platform,
            Source = "dictation-send",
            DurationSeconds = 20,
            SampleRate = 48000,
            SpeechLevelDb = speechDb,
            NoiseFloorDb = speechDb - snrDb,
            SignalToNoiseDb = snrDb,
            ClippedFraction = clipped,
            HighBandRatioDb = narrowband ? -110 : -22,
            Narrowband = narrowband,
            Rating = narrowband || clipped >= 0.01 ? "poor" : "good",
            Issues = narrowband ? "narrowband" : clipped >= 0.01 ? "clipping" : "",
        };

    private static List<MicrophoneQualityRecord> Many(int count, Func<int, MicrophoneQualityRecord> make)
        => Enumerable.Range(0, count).Select(make).ToList();

    [Fact]
    public void NoMeasurementsYet_SaysSo_AndDoesNotPretendAnythingIsWrong()
    {
        var s = MicrophoneQualityFold.Summarize(Array.Empty<MicrophoneQualityRecord>());

        Assert.Equal("empty", s.Status);
        Assert.Equal(0, s.TotalSamples);
        Assert.Empty(s.Devices);
        Assert.Contains("No dictation has been measured", s.Headline);
    }

    [Fact]
    public void AHealthyMicrophoneIsNeverComplainedAbout()
    {
        // The property that matters most. This is the shape of the real 212-clip sample.
        var s = MicrophoneQualityFold.Summarize(Many(40, _ => Sample()));

        Assert.Equal("good", s.Status);
        Assert.Contains("sounds good", s.Headline);
        Assert.Equal("good", Assert.Single(s.Devices).Status);
    }

    [Fact]
    public void TooFewMeasurementsIsHeldBack_RatherThanJudgedOnOneOddRecording()
    {
        var s = MicrophoneQualityFold.Summarize(Many(MicrophoneQualityFold.MinSamplesForVerdict - 1, _ => Sample(narrowband: true)));

        // Narrowband on every clip, but not enough of them: it must NOT accuse yet.
        Assert.Equal("learning", s.Status);
        Assert.Equal("learning", Assert.Single(s.Devices).Status);
    }

    [Fact]
    public void AMostlyBandLimitedMicrophoneIsNamedAndExplained()
    {
        var s = MicrophoneQualityFold.Summarize(Many(20, _ => Sample(device: "Jabra Hands-Free", narrowband: true)));

        Assert.Equal("bad", s.Status);
        Assert.Contains("Jabra Hands-Free", s.Headline);
        Assert.Contains("Bluetooth", s.Detail);
    }

    [Fact]
    public void OccasionalBandLimitingIsNotEnoughToAccuse()
    {
        // A Bluetooth link that dropped to hands-free for one call is not a bad headset.
        var s = MicrophoneQualityFold.Summarize(Many(20, i => Sample(narrowband: i < 3)));

        Assert.Equal("good", s.Status);
    }

    [Fact]
    public void PersistentDistortionIsCalledOut()
    {
        var s = MicrophoneQualityFold.Summarize(Many(20, i => Sample(device: "Hot Mic", clipped: i < 10 ? 0.05 : 0)));

        Assert.Equal("bad", s.Status);
        Assert.Contains("Hot Mic", s.Headline);
        Assert.Contains("input level", s.Detail);
    }

    [Fact]
    public void QuietOrNoisyAudioAloneNeverProducesAWarning()
    {
        // Deliberate: on the real sample these fired on about one dictation in nine. They are shown
        // as measurements on the device card, never as an accusation.
        var s = MicrophoneQualityFold.Summarize(Many(30, _ => Sample(speechDb: -35, snrDb: 14)));

        Assert.Equal("good", s.Status);
        Assert.Equal("good", Assert.Single(s.Devices).Status);
    }

    [Fact]
    public void TheWorstMicrophoneLeadsRatherThanTheAverageOfAllOfThem()
    {
        // The finding a user needs is "one of your microphones is the problem". Averaging a bad
        // headset with a good desk microphone would hide exactly that.
        var records = Many(20, _ => Sample(device: "Good Desk Mic"))
            .Concat(Many(20, _ => Sample(device: "Bad Headset", narrowband: true)))
            .ToList();

        var s = MicrophoneQualityFold.Summarize(records);

        Assert.Equal("bad", s.Status);
        Assert.Contains("Bad Headset", s.Headline);
        Assert.Equal(2, s.Devices.Count);
        Assert.Equal("good", s.Devices.Single(d => d.Device == "Good Desk Mic").Status);
        Assert.Equal("bad", s.Devices.Single(d => d.Device == "Bad Headset").Status);
    }

    [Fact]
    public void EachDeviceIsScoredSeparately_WhichIsThePointOfRecordingTheName()
    {
        var records = Many(10, _ => Sample(device: "A"))
            .Concat(Many(6, _ => Sample(device: "B", clipped: 0.05)))
            .ToList();

        var s = MicrophoneQualityFold.Summarize(records);

        Assert.Equal(1.0, s.Devices.Single(d => d.Device == "B").ClippingShare);
        Assert.Equal(0.0, s.Devices.Single(d => d.Device == "A").ClippingShare);
    }

    [Fact]
    public void AnUnnamedMicrophoneIsStillReported_UnderAReadableName()
    {
        var s = MicrophoneQualityFold.Summarize(Many(10, _ => Sample(device: "")));

        Assert.Equal("Unnamed microphone", Assert.Single(s.Devices).Device);
    }

    [Fact]
    public void EveryDeviceCarriesWhatGoodLooksLike_SoTheScreenCanCompare()
    {
        var device = Assert.Single(MicrophoneQualityFold.Summarize(Many(10, _ => Sample())).Devices);

        Assert.Equal(MicrophoneQualityFold.TargetSpeechLevelDb, device.TargetSpeechLevelDb);
        Assert.Equal(MicrophoneQualityFold.TargetSignalToNoiseDb, device.TargetSignalToNoiseDb);
    }

    [Fact]
    public void TheMedianIsReported_SoOneOddRecordingCannotMoveTheNumber()
    {
        var records = Many(20, _ => Sample(speechDb: -20)).Append(Sample(speechDb: -90)).ToList();

        var device = Assert.Single(MicrophoneQualityFold.Summarize(records).Devices);

        Assert.Equal(-20, device.MedianSpeechLevelDb);
    }

    // ---- device identity: the grouping key is the ID, the name is display metadata (#2183) ----

    [Fact]
    public void ARenamedDeviceKeepsOneHistory_BecauseTheIdIsTheGroupingKey()
    {
        // A driver update or an operating system language change renames the device. Grouped by
        // name, that would silently split one microphone into two histories - the exact failure
        // the deviceId exists to prevent.
        var records = Many(6, i => Sample(device: "Mikrofonarray (Realtek)", deviceId: "id-1", minutesAgo: i))
            .Concat(Many(6, i => Sample(device: "Microphone Array (Realtek)", deviceId: "id-1", minutesAgo: 100 + i)))
            .ToList();

        var device = Assert.Single(MicrophoneQualityFold.Summarize(records).Devices);
        Assert.Equal(12, device.Samples);
        // The newest name is the one the operating system currently uses.
        Assert.Equal("Mikrofonarray (Realtek)", device.Device);
        Assert.Equal("id-1", device.DeviceId);
    }

    [Fact]
    public void TwoDevicesSharingANameAreStillTwoRows_WhenTheirIdsDiffer()
    {
        // Two identical USB microphones report the same label; only the id can tell them apart.
        var records = Many(6, i => Sample(device: "USB Microphone", deviceId: "id-left", minutesAgo: i))
            .Concat(Many(6, i => Sample(device: "USB Microphone", deviceId: "id-right", narrowband: true, minutesAgo: i)))
            .ToList();

        var devices = MicrophoneQualityFold.Summarize(records).Devices;
        Assert.Equal(2, devices.Count);
        Assert.Equal("good", devices.Single(d => d.DeviceId == "id-left").Status);
        Assert.Equal("bad", devices.Single(d => d.DeviceId == "id-right").Status);
    }

    [Fact]
    public void LegacyRecordsWithoutAnIdJoinTheMatchingIdGroup_SoShippingTheIdResetsNoHistory()
    {
        // Records written before the id existed carry only the name. When exactly one id-group
        // wears that name, they belong to it - otherwise every device restarts at "learning" the
        // day the id ships.
        var records = Many(4, i => Sample(device: "Desk Mic", minutesAgo: 500 + i))
            .Concat(Many(3, i => Sample(device: "Desk Mic", deviceId: "id-desk", minutesAgo: i)))
            .ToList();

        var device = Assert.Single(MicrophoneQualityFold.Summarize(records).Devices);
        Assert.Equal(7, device.Samples);
        Assert.Equal("id-desk", device.DeviceId);
        // Seven measurements clear the verdict bar; a split would have left 4 + 3, both "learning".
        Assert.Equal("good", device.Status);
    }

    [Fact]
    public void LegacyRecordsStayTheirOwnRowWhenTwoIdGroupsShareTheName()
    {
        // Ambiguous adoption would file measurements under the WRONG microphone, which is worse
        // than an extra row that ages out with the retention window.
        var records = Many(2, i => Sample(device: "USB Microphone", minutesAgo: 500 + i))
            .Concat(Many(3, i => Sample(device: "USB Microphone", deviceId: "id-a", minutesAgo: i)))
            .Concat(Many(3, i => Sample(device: "USB Microphone", deviceId: "id-b", minutesAgo: i)))
            .ToList();

        Assert.Equal(3, MicrophoneQualityFold.Summarize(records).Devices.Count);
    }

    // ---- platform classification ----

    [Fact]
    public void EachDeviceCarriesItsPlatform_ReadyToRender()
    {
        var records = Many(6, i => Sample(device: "Phone Mic", deviceId: "id-p", platform: "mobile", minutesAgo: i))
            .Concat(Many(6, i => Sample(device: "Desk Mic", deviceId: "id-d", platform: "windows", minutesAgo: i)))
            .ToList();

        var devices = MicrophoneQualityFold.Summarize(records).Devices;
        var phone = devices.Single(d => d.DeviceId == "id-p");
        Assert.Equal("mobile", phone.Platform);
        Assert.Equal("Phone or tablet", phone.PlatformLabel);
        var desk = devices.Single(d => d.DeviceId == "id-d");
        Assert.Equal("windows", desk.Platform);
        Assert.Equal("Windows", desk.PlatformLabel);
    }

    [Fact]
    public void AnUnrecognisedPlatformReadsAsUnknown_NeverAsAGuessedBucket()
    {
        var bogus = Assert.Single(MicrophoneQualityFold.Summarize(
            Many(6, i => Sample(deviceId: "id-x", platform: "amiga", minutesAgo: i))).Devices);
        Assert.Equal("unknown", bogus.Platform);
        Assert.Equal("", bogus.PlatformLabel);

        var legacy = Assert.Single(MicrophoneQualityFold.Summarize(
            Many(6, i => Sample(deviceId: "id-y", minutesAgo: i))).Devices);
        Assert.Equal("unknown", legacy.Platform);
    }

    // ---- the detail view: measurements + quality over time ----

    [Fact]
    public void TheDetailAgreesWithTheSummary_TheyShareEveryFold()
    {
        var records = Many(10, i => Sample(deviceId: "id-1", minutesAgo: i))
            .Concat(Many(10, i => Sample(device: "Bad Headset", deviceId: "id-2", narrowband: true, minutesAgo: i)))
            .ToList();

        var summary = MicrophoneQualityFold.Summarize(records);
        var detail = MicrophoneQualityFold.Detail(records);

        Assert.Equal(summary.Status, detail.Status);
        Assert.Equal(summary.Headline, detail.Headline);
        Assert.Equal(summary.TotalSamples, detail.TotalSamples);
        Assert.Equal(summary.Devices.Select(d => d.DeviceId), detail.Devices.Select(d => d.Summary.DeviceId));
    }

    [Fact]
    public void TheDetailCarriesEveryMeasurementNewestFirst_AndOnePointPerDayOldestFirst()
    {
        // Three dictations yesterday, two today - the measurement list is the evidence, the daily
        // points are the trend a chart draws left to right.
        var records = Many(3, i => Sample(deviceId: "id-1", snrDb: 30, minutesAgo: 1500 + i))
            .Concat(Many(2, i => Sample(deviceId: "id-1", snrDb: 40, minutesAgo: i)))
            .ToList();

        var device = Assert.Single(MicrophoneQualityFold.Detail(records).Devices);

        Assert.Equal(5, device.MeasurementsTotal);
        Assert.Equal(5, device.Measurements.Count);
        Assert.True(device.Measurements.First().TimestampUtc >= device.Measurements.Last().TimestampUtc);

        Assert.Equal(2, device.Trend.Count);
        Assert.True(string.CompareOrdinal(device.Trend[0].Date, device.Trend[1].Date) < 0);
        Assert.Equal(3, device.Trend[0].Samples);
        Assert.Equal(30, device.Trend[0].MedianSignalToNoiseDb);
        Assert.Equal(40, device.Trend[1].MedianSignalToNoiseDb);
    }

    [Fact]
    public void TheMeasurementListIsCappedLoudly_TheTotalStillTellsTheTruth()
    {
        var records = Many(MicrophoneQualityFold.MaxDetailMeasurements + 25,
            i => Sample(deviceId: "id-1", minutesAgo: i));

        var device = Assert.Single(MicrophoneQualityFold.Detail(records).Devices);

        Assert.Equal(MicrophoneQualityFold.MaxDetailMeasurements, device.Measurements.Count);
        Assert.Equal(MicrophoneQualityFold.MaxDetailMeasurements + 25, device.MeasurementsTotal);
    }

    [Fact]
    public void TwoMicrophonesOnTwoPlatformsAreTwoRows_NamedAndClassified()
    {
        // The acceptance line of issue #2183: two physical microphones on two platforms produce two
        // rows, correctly named and correctly classified - never one averaged "Default" row.
        var records = Many(8, i => Sample(device: "iPhone Microphone", deviceId: "id-phone", platform: "mobile", minutesAgo: i))
            .Concat(Many(8, i => Sample(device: "Headset (Jabra Evolve2 65)", deviceId: "id-jabra", platform: "windows", narrowband: true, minutesAgo: i)))
            .ToList();

        var detail = MicrophoneQualityFold.Detail(records);

        Assert.Equal(2, detail.Devices.Count);
        var phone = detail.Devices.Single(d => d.Summary.DeviceId == "id-phone");
        Assert.Equal("iPhone Microphone", phone.Summary.Device);
        Assert.Equal("mobile", phone.Summary.Platform);
        Assert.Equal("good", phone.Summary.Status);
        var jabra = detail.Devices.Single(d => d.Summary.DeviceId == "id-jabra");
        Assert.Equal("Headset (Jabra Evolve2 65)", jabra.Summary.Device);
        Assert.Equal("windows", jabra.Summary.Platform);
        Assert.Equal("bad", jabra.Summary.Status);
    }
}
