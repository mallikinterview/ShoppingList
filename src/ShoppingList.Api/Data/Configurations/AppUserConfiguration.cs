using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShoppingList.Api.Data.Entities;

namespace ShoppingList.Api.Data.Configurations;

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.ToTable("users");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.SubjectId)
            .HasMaxLength(64)
            .IsRequired();

        // Unique, and the reason just-in-time provisioning is safe under concurrency: two
        // simultaneous first requests from the same new user both attempt an insert, and this
        // index makes one of them lose deterministically rather than creating a duplicate
        // identity with a split item history.
        builder.HasIndex(x => x.SubjectId)
            .IsUnique()
            .HasDatabaseName("ux_users_subject_id");

        builder.Property(x => x.Username).HasMaxLength(64);
        builder.Property(x => x.Email).HasMaxLength(254);

        builder.HasMany(x => x.Items)
            .WithOne(x => x.User)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ItemImageConfiguration : IEntityTypeConfiguration<ItemImage>
{
    public void Configure(EntityTypeBuilder<ItemImage> builder)
    {
        builder.ToTable("item_images");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.ObjectKey)
            .HasMaxLength(512)
            .IsRequired();

        // Unique: the same object must never be referenced by two rows, or deleting one item
        // removes an image another still points at.
        builder.HasIndex(x => x.ObjectKey)
            .IsUnique()
            .HasDatabaseName("ux_item_images_object_key");

        builder.Property(x => x.ContentType)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(x => x.OriginalFileName).HasMaxLength(128);

        builder.HasIndex(x => x.ItemId)
            .HasDatabaseName("ix_item_images_item_id");
    }
}
