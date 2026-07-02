// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;
using OrgZ.Services.Media;

namespace OrgZ.Tests;

/// <summary>
/// Round-trips the shared acquisition record - the durable "I got this" layer both podcasts and
/// audiobooks hang on. Redirects the DB to a temp directory so it never touches the real
/// library.db. Identity is (Kind, SourceKey); the same key under two kinds must not collide, and
/// re-acquiring must refresh the mutable fields without resetting AcquiredAt.
/// </summary>
[Collection("AcquisitionStore")]
public class AcquisitionStoreTests : IDisposable
{
    private readonly string _tempDir;

    public AcquisitionStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OrgZ-AcquisitionStore-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        AcquisitionStore.OverrideCacheDirectory(_tempDir);
        AcquisitionStore.EnsureCreated();
    }

    public void Dispose()
    {
        AcquisitionStore.OverrideCacheDirectory(null);
        try { if (Directory.Exists(_tempDir)) { Directory.Delete(_tempDir, recursive: true); } } catch { }
    }

    private static AcquiredMedia Book(string key, string? title = null, bool userProvided = false, DateTime acquiredAt = default) => new()
    {
        Kind           = AcquiredMediaKind.Audiobook,
        SourceKey      = key,
        Title          = title ?? $"Book {key}",
        Creator        = "Author",
        ImageUrl       = $"https://archive.org/services/img/{key}",
        SourceRefJson  = userProvided ? null : $"{{\"id\":\"{key}\"}}",
        IsUserProvided = userProvided,
        AcquiredAt     = acquiredAt,
    };

    [Fact]
    public void Acquire_then_Get_round_trips_all_fields()
    {
        var when = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        AcquisitionStore.Acquire(Book("mobydick", "Moby Dick", acquiredAt: when));

        var got = AcquisitionStore.Get(AcquiredMediaKind.Audiobook, "mobydick");

        Assert.NotNull(got);
        Assert.Equal(AcquiredMediaKind.Audiobook, got!.Kind);
        Assert.Equal("mobydick", got.SourceKey);
        Assert.Equal("Moby Dick", got.Title);
        Assert.Equal("Author", got.Creator);
        Assert.Equal("https://archive.org/services/img/mobydick", got.ImageUrl);
        Assert.Equal("{\"id\":\"mobydick\"}", got.SourceRefJson);
        Assert.False(got.IsUserProvided);
        Assert.Equal(when, got.AcquiredAt);
    }

    [Fact]
    public void IsAcquired_reflects_presence()
    {
        Assert.False(AcquisitionStore.IsAcquired(AcquiredMediaKind.Audiobook, "x"));
        AcquisitionStore.Acquire(Book("x"));
        Assert.True(AcquisitionStore.IsAcquired(AcquiredMediaKind.Audiobook, "x"));
    }

    [Fact]
    public void Release_forgets_the_acquisition()
    {
        AcquisitionStore.Acquire(Book("x"));
        AcquisitionStore.Release(AcquiredMediaKind.Audiobook, "x");
        Assert.False(AcquisitionStore.IsAcquired(AcquiredMediaKind.Audiobook, "x"));
        Assert.Null(AcquisitionStore.Get(AcquiredMediaKind.Audiobook, "x"));
    }

    [Fact]
    public void Kind_and_SourceKey_form_the_identity_no_cross_kind_collision()
    {
        // Same SourceKey under two kinds are two distinct rows.
        AcquisitionStore.Acquire(new AcquiredMedia { Kind = AcquiredMediaKind.Podcast,   SourceKey = "42", Title = "A Show" });
        AcquisitionStore.Acquire(new AcquiredMedia { Kind = AcquiredMediaKind.Audiobook, SourceKey = "42", Title = "A Book" });

        Assert.Equal("A Show", AcquisitionStore.Get(AcquiredMediaKind.Podcast,   "42")!.Title);
        Assert.Equal("A Book", AcquisitionStore.Get(AcquiredMediaKind.Audiobook, "42")!.Title);
    }

    [Fact]
    public void Acquire_again_refreshes_mutable_fields_but_keeps_original_AcquiredAt()
    {
        var first = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        AcquisitionStore.Acquire(Book("x", "Old Title", acquiredAt: first));

        var later = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        AcquisitionStore.Acquire(Book("x", "New Title", acquiredAt: later));

        var got = AcquisitionStore.Get(AcquiredMediaKind.Audiobook, "x")!;
        Assert.Equal("New Title", got.Title);       // mutable field refreshed
        Assert.Equal(first, got.AcquiredAt);        // first-acquired timestamp preserved
    }

    [Fact]
    public void GetAll_returns_only_the_kind_newest_first()
    {
        AcquisitionStore.Acquire(Book("older", acquiredAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        AcquisitionStore.Acquire(Book("newer", acquiredAt: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)));
        AcquisitionStore.Acquire(new AcquiredMedia { Kind = AcquiredMediaKind.Podcast, SourceKey = "99", Title = "Show" });

        var books = AcquisitionStore.GetAll(AcquiredMediaKind.Audiobook);

        Assert.Equal(2, books.Count);
        Assert.Equal("newer", books[0].SourceKey);
        Assert.Equal("older", books[1].SourceKey);
    }

    [Fact]
    public void AcquiredAt_defaults_to_now_when_unset()
    {
        AcquisitionStore.Acquire(Book("x"));   // acquiredAt left default
        var got = AcquisitionStore.Get(AcquiredMediaKind.Audiobook, "x")!;
        Assert.NotEqual(default, got.AcquiredAt);
    }

    [Fact]
    public void User_provided_item_persists_with_no_source()
    {
        AcquisitionStore.Acquire(Book("local:abc", "My Own Book", userProvided: true));
        var got = AcquisitionStore.Get(AcquiredMediaKind.Audiobook, "local:abc")!;
        Assert.True(got.IsUserProvided);
        Assert.Null(got.SourceRefJson);
    }

    [Fact]
    public void EnsureCreated_is_idempotent_and_preserves_existing_rows()
    {
        AcquisitionStore.Acquire(Book("keep", "Keep Me"));
        AcquisitionStore.EnsureCreated();   // a second schema pass must neither throw nor wipe data
        Assert.Equal("Keep Me", AcquisitionStore.Get(AcquiredMediaKind.Audiobook, "keep")!.Title);
    }

    [Fact]
    public void Release_of_a_row_that_isnt_there_is_a_safe_no_op()
    {
        AcquisitionStore.Release(AcquiredMediaKind.Audiobook, "ghost");   // must not throw
        Assert.False(AcquisitionStore.IsAcquired(AcquiredMediaKind.Audiobook, "ghost"));
    }

    [Fact]
    public void GetAll_is_empty_when_nothing_acquired()
    {
        Assert.Empty(AcquisitionStore.GetAll(AcquiredMediaKind.Podcast));
        Assert.Empty(AcquisitionStore.GetAll(AcquiredMediaKind.Audiobook));
    }

    [Fact]
    public void A_minimal_record_round_trips_with_null_optionals()
    {
        AcquisitionStore.Acquire(new AcquiredMedia { Kind = AcquiredMediaKind.Audiobook, SourceKey = "bare" });
        var got = AcquisitionStore.Get(AcquiredMediaKind.Audiobook, "bare")!;
        Assert.Null(got.Title);
        Assert.Null(got.Creator);
        Assert.Null(got.ImageUrl);
        Assert.Null(got.HomepageUrl);
        Assert.Null(got.SourceRefJson);
        Assert.False(got.IsUserProvided);
    }

    [Fact]
    public void Re_acquiring_a_user_file_from_a_store_flips_it_to_source_backed()
    {
        // You dropped a book in yourself, then later got the same title from a store: the record
        // must gain a re-fetch source and stop being user-provided (so re-download becomes offerable).
        AcquisitionStore.Acquire(Book("dual", "Dual", userProvided: true));
        Assert.True(AcquisitionStore.Get(AcquiredMediaKind.Audiobook, "dual")!.IsUserProvided);

        AcquisitionStore.Acquire(Book("dual", "Dual", userProvided: false));
        var got = AcquisitionStore.Get(AcquiredMediaKind.Audiobook, "dual")!;
        Assert.False(got.IsUserProvided);
        Assert.NotNull(got.SourceRefJson);
    }
}
