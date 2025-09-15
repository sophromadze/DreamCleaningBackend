using Microsoft.EntityFrameworkCore;
using DreamCleaningBackend.Models;

namespace DreamCleaningBackend.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<Apartment> Apartments { get; set; }
        public DbSet<Subscription> Subscriptions { get; set; }
        public DbSet<Order> Orders { get; set; }
        public DbSet<ServiceType> ServiceTypes { get; set; }
        public DbSet<Service> Services { get; set; }
        public DbSet<ExtraService> ExtraServices { get; set; }
        public DbSet<OrderService> OrderServices { get; set; }
        public DbSet<OrderExtraService> OrderExtraServices { get; set; }
        public DbSet<PromoCode> PromoCodes { get; set; }
        public DbSet<GiftCard> GiftCards { get; set; }
        public DbSet<GiftCardUsage> GiftCardUsages { get; set; }
        public DbSet<AuditLog> AuditLogs { get; set; }
		public DbSet<SpecialOffer> SpecialOffers { get; set; }
        public DbSet<UserSpecialOffer> UserSpecialOffers { get; set; }
        public DbSet<GiftCardConfig> GiftCardConfigs { get; set; }
        public DbSet<OrderCleaner> OrderCleaners { get; set; }
        public DbSet<NotificationLog> NotificationLogs { get; set; }
        public DbSet<PollQuestion> PollQuestions { get; set; }
        public DbSet<PollSubmission> PollSubmissions { get; set; }
        public DbSet<PollAnswer> PollAnswers { get; set; }
        public DbSet<OrderUpdateHistory> OrderUpdateHistories { get; set; }
        public DbSet<MaintenanceMode> MaintenanceModes { get; set; }
        public DbSet<WebhookEvent> WebhookEvents { get; set; }
        public DbSet<ScheduledMail> ScheduledMails { get; set; }
        public DbSet<SentMailLog> SentMailLogs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // AuditLog configuration
            modelBuilder.Entity<AuditLog>(entity =>
            {
                entity.HasIndex(e => new { e.EntityType, e.EntityId })
                    .HasDatabaseName("IX_AuditLogs_Entity");

                entity.HasIndex(e => e.CreatedAt)
                    .HasDatabaseName("IX_AuditLogs_CreatedAt");

                entity.HasIndex(e => e.UserId)
                    .HasDatabaseName("IX_AuditLogs_UserId");
            });

            // User configuration
            modelBuilder.Entity<User>()
                .HasIndex(u => u.Email)
                .IsUnique();

            modelBuilder.Entity<User>()
                .Property(u => u.FirstTimeOrder)
                .HasDefaultValue(true);

            // Apartment configuration
            modelBuilder.Entity<Apartment>()
                .HasOne(a => a.User)
                .WithMany(u => u.Apartments)
                .HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Subscription configuration
            modelBuilder.Entity<Order>()
                .HasOne(o => o.Subscription)
                .WithMany(s => s.Orders)
                .HasForeignKey(o => o.SubscriptionId)
                .OnDelete(DeleteBehavior.Restrict);

            // SpecialOffer configuration
            modelBuilder.Entity<UserSpecialOffer>()
                .HasIndex(u => new { u.UserId, u.SpecialOfferId })
                .IsUnique();

            modelBuilder.Entity<UserSpecialOffer>()
                .HasOne(uso => uso.UsedOnOrder)
                .WithMany()
                .HasForeignKey(uso => uso.UsedOnOrderId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);

            // This tells EF that the relationship is optional and prevents conflicts
            modelBuilder.Entity<UserSpecialOffer>()
                .Property(uso => uso.UsedOnOrderId)
                .IsRequired(false);

            // Order configuration
            modelBuilder.Entity<Order>()
                .HasOne(o => o.User)
                .WithMany(u => u.Orders)
                .HasForeignKey(o => o.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Order>()
                .HasOne(o => o.Apartment)
                .WithMany()
                .HasForeignKey(o => o.ApartmentId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<Order>()
                .HasOne(o => o.ServiceType)
                .WithMany(st => st.Orders)
                .HasForeignKey(o => o.ServiceTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            // OrderCleaner configuration
            modelBuilder.Entity<OrderCleaner>(entity =>
            {
                entity.HasKey(oc => oc.Id);

                entity.HasOne(oc => oc.Order)
                      .WithMany(o => o.OrderCleaners)
                      .HasForeignKey(oc => oc.OrderId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(oc => oc.Cleaner)
                      .WithMany()
                      .HasForeignKey(oc => oc.CleanerId)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(oc => oc.AssignedByUser)
                      .WithMany()
                      .HasForeignKey(oc => oc.AssignedBy)
                      .OnDelete(DeleteBehavior.Restrict);
            });

            // OrderUpdateHistory configuration
            modelBuilder.Entity<OrderUpdateHistory>()
                .HasOne(ouh => ouh.Order)
                .WithMany(o => o.UpdateHistory)
                .HasForeignKey(ouh => ouh.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<OrderUpdateHistory>()
                .HasOne(ouh => ouh.UpdatedByUser)
                .WithMany()
                .HasForeignKey(ouh => ouh.UpdatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<OrderUpdateHistory>()
                .Property(ouh => ouh.IsPaid)
                .HasDefaultValue(false);

            // Poll configuration
            modelBuilder.Entity<PollQuestion>()
                .HasOne(pq => pq.ServiceType)
                .WithMany(st => st.PollQuestions)
                .HasForeignKey(pq => pq.ServiceTypeId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<PollSubmission>()
                .HasOne(ps => ps.User)
                .WithMany()
                .HasForeignKey(ps => ps.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<PollSubmission>()
                .HasOne(ps => ps.ServiceType)
                .WithMany()
                .HasForeignKey(ps => ps.ServiceTypeId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<PollAnswer>()
                .HasOne(pa => pa.PollSubmission)
                .WithMany(ps => ps.PollAnswers)
                .HasForeignKey(pa => pa.PollSubmissionId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<PollAnswer>()
                .HasOne(pa => pa.PollQuestion)
                .WithMany(pq => pq.PollAnswers)
                .HasForeignKey(pa => pa.PollQuestionId)
                .OnDelete(DeleteBehavior.Restrict);
        

        // Service configuration
        modelBuilder.Entity<Service>()
                .HasOne(s => s.ServiceType)
                .WithMany(st => st.Services)
                .HasForeignKey(s => s.ServiceTypeId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Service>()
                .HasIndex(s => s.ServiceKey);

            // ExtraService configuration
            modelBuilder.Entity<ExtraService>()
                .HasOne(es => es.ServiceType)
                .WithMany(st => st.ExtraServices)
                .HasForeignKey(es => es.ServiceTypeId)
                .OnDelete(DeleteBehavior.SetNull);

            // OrderService configuration
            modelBuilder.Entity<OrderService>()
                .HasOne(os => os.Order)
                .WithMany(o => o.OrderServices)
                .HasForeignKey(os => os.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<OrderService>()
                .HasOne(os => os.Service)
                .WithMany(s => s.OrderServices)
                .HasForeignKey(os => os.ServiceId)
                .OnDelete(DeleteBehavior.Restrict);

            // OrderExtraService configuration
            modelBuilder.Entity<OrderExtraService>()
                .HasOne(oes => oes.Order)
                .WithMany(o => o.OrderExtraServices)
                .HasForeignKey(oes => oes.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<OrderExtraService>()
                .HasOne(oes => oes.ExtraService)
                .WithMany(es => es.OrderExtraServices)
                .HasForeignKey(oes => oes.ExtraServiceId)
                .OnDelete(DeleteBehavior.Restrict);

            // PromoCode configuration
            modelBuilder.Entity<PromoCode>()
                .HasIndex(p => p.Code)
                .IsUnique();

            // Seed initial subscriptions
            modelBuilder.Entity<Subscription>().HasData(
                 new Subscription
                 {
                     Id = 1,
                     Name = "One Time",
                     Description = "Single cleaning service",
                     SubscriptionDays = 0,
                     DiscountPercentage = 0,
                     DisplayOrder = 1,
                     CreatedAt = DateTime.UtcNow
                 },
                new Subscription
                {
                    Id = 2,
                    Name = "Weekly",
                    Description = "Cleaning every week",
                    SubscriptionDays = 7,
                    DiscountPercentage = 15,
                    DisplayOrder = 2,
                    CreatedAt = DateTime.UtcNow
                },
                new Subscription
                {
                    Id = 3,
                    Name = "Bi-Weekly",
                    Description = "Cleaning every two weeks",
                    SubscriptionDays = 14,
                    DiscountPercentage = 10,
                    DisplayOrder = 3,
                    CreatedAt = DateTime.UtcNow
                },
                new Subscription
                {
                    Id = 4,
                    Name = "Monthly",
                    Description = "Cleaning once a month",
                    SubscriptionDays = 30,
                    DiscountPercentage = 5,
                    DisplayOrder = 4,
                    CreatedAt = DateTime.UtcNow
                }
            );

            // Seed Service Types
            modelBuilder.Entity<ServiceType>().HasData(
                new ServiceType
                {
                    Id = 1,
                    Name = "Residential Cleaning",
                    BasePrice = 120,
                    Description = "Complete home cleaning service",
                    DisplayOrder = 1,
                    IsActive = true,
                    TimeDuration = 90,
                    CreatedAt = DateTime.UtcNow
                },
                new ServiceType
                {
                    Id = 2,
                    Name = "Office Cleaning",
                    BasePrice = 200,
                    Description = "Professional office cleaning service",
                    DisplayOrder = 2,
                    IsActive = true,
                    TimeDuration = 120,
                    CreatedAt = DateTime.UtcNow
                }
            );

            // Gift Card configurations
            modelBuilder.Entity<GiftCard>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Code)
                    .IsRequired()
                    .HasMaxLength(14);

                entity.HasIndex(e => e.Code)
                    .IsUnique();

                entity.Property(e => e.OriginalAmount)
                    .HasColumnType("decimal(10,2)")
                    .IsRequired();

                entity.Property(e => e.CurrentBalance)
                    .HasColumnType("decimal(10,2)")
                    .IsRequired();

                entity.Property(e => e.RecipientName)
                    .IsRequired()
                    .HasMaxLength(100);

                entity.Property(e => e.RecipientEmail)
                    .IsRequired()
                    .HasMaxLength(255);

                entity.Property(e => e.SenderName)
                    .IsRequired()
                    .HasMaxLength(100);

                entity.Property(e => e.SenderEmail)
                    .IsRequired()
                    .HasMaxLength(255);

                entity.Property(e => e.Message)
                    .HasMaxLength(500);

                entity.Property(e => e.PaymentIntentId)
                    .HasMaxLength(100);

                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");

                // Foreign key relationships
                entity.HasOne(e => e.PurchasedByUser)
                    .WithMany()
                    .HasForeignKey(e => e.PurchasedByUserId)
                    .OnDelete(DeleteBehavior.Restrict);

                // REMOVED: UsedByUser relationship
            });

            // Gift Card Usage configurations
            modelBuilder.Entity<GiftCardUsage>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.Property(e => e.AmountUsed)
                    .HasColumnType("decimal(10,2)")
                    .IsRequired();

                entity.Property(e => e.BalanceAfterUsage)
                    .HasColumnType("decimal(10,2)")
                    .IsRequired();

                entity.Property(e => e.UsedAt)
                    .HasDefaultValueSql("CURRENT_TIMESTAMP");

                // Foreign key relationships
                entity.HasOne(e => e.GiftCard)
                    .WithMany(g => g.GiftCardUsages)
                    .HasForeignKey(e => e.GiftCardId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(e => e.Order)
                    .WithMany()
                    .HasForeignKey(e => e.OrderId)
                    .OnDelete(DeleteBehavior.Restrict);

                // ADD: User relationship
                entity.HasOne(e => e.User)
                    .WithMany()
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            // ScheduledMail configuration
            modelBuilder.Entity<ScheduledMail>(entity =>
    {
        entity.HasIndex(e => e.Status)
            .HasDatabaseName("IX_ScheduledMails_Status");

        entity.HasIndex(e => e.NextScheduledAt)
            .HasDatabaseName("IX_ScheduledMails_NextScheduledAt");

        entity.HasOne(e => e.CreatedBy)
            .WithMany()
            .HasForeignKey(e => e.CreatedById)
            .OnDelete(DeleteBehavior.Restrict);

        entity.Property(e => e.TargetRoles)
            .IsRequired();

        entity.Property(e => e.Subject)
            .IsRequired()
            .HasMaxLength(200);

        entity.Property(e => e.Content)
            .IsRequired();
    });


            // SentMailLog configuration
            modelBuilder.Entity<SentMailLog>(entity =>
    {
        entity.HasIndex(e => new { e.ScheduledMailId, e.SentAt })
            .HasDatabaseName("IX_SentMailLogs_MailId_SentAt");

        entity.HasOne(e => e.ScheduledMail)
            .WithMany(m => m.SentMailLogs)
            .HasForeignKey(e => e.ScheduledMailId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.Property(e => e.RecipientEmail)
            .IsRequired()
            .HasMaxLength(256);

        entity.Property(e => e.RecipientName)
            .HasMaxLength(200);

        entity.Property(e => e.RecipientRole)
            .HasMaxLength(50);
    });

            // Update Order entity for gift card tracking
            modelBuilder.Entity<Order>(entity =>
            {
                // ... existing configurations ...

                entity.Property(e => e.GiftCardAmountUsed)
                    .HasColumnType("decimal(10,2)")
                    .HasDefaultValue(0);

                entity.Property(e => e.GiftCardCode)
                    .HasMaxLength(14);
            });

            // Seed Services for Residential Cleaning
            modelBuilder.Entity<Service>().HasData(
                new Service
                {
                    Id = 1,
                    Name = "Bedrooms",
                    ServiceKey = "bedrooms",
                    Cost = 25,
                    TimeDuration = 30,
                    ServiceTypeId = 1,
                    InputType = "dropdown",
                    MinValue = 0,
                    MaxValue = 6,
                    StepValue = 1,
                    DisplayOrder = 1,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new Service
                {
                    Id = 2,
                    Name = "Bathrooms",
                    ServiceKey = "bathrooms",
                    Cost = 35,
                    TimeDuration = 45,
                    ServiceTypeId = 1,
                    InputType = "dropdown",
                    MinValue = 1,
                    MaxValue = 5,
                    StepValue = 1,
                    DisplayOrder = 2,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new Service
                {
                    Id = 3,
                    Name = "Square Feet",
                    ServiceKey = "sqft",
                    Cost = 0.10m, // per square foot
                    TimeDuration = 1, // per 100 sqft
                    ServiceTypeId = 1,
                    InputType = "slider",
                    MinValue = 400,
                    MaxValue = 5000,
                    StepValue = 100,
                    IsRangeInput = true,
                    Unit = "per sqft",
                    DisplayOrder = 3,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                }
            );

            // Seed Services for Office Cleaning
            modelBuilder.Entity<Service>().HasData(
                new Service
                {
                    Id = 4,
                    Name = "Cleaners",
                    ServiceKey = "cleaners",
                    Cost = 40,
                    TimeDuration = 0,
                    ServiceTypeId = 2,
                    InputType = "dropdown",
                    MinValue = 1,
                    MaxValue = 10,
                    StepValue = 1,
                    Unit = "per hour",
                    ServiceRelationType = "cleaner",
                    DisplayOrder = 1,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new Service
                {
                    Id = 5,
                    Name = "Hours",
                    ServiceKey = "hours",
                    Cost = 0, // Cost is per cleaner
                    TimeDuration = 60,
                    ServiceTypeId = 2,
                    InputType = "dropdown",
                    MinValue = 2,
                    MaxValue = 8,
                    StepValue = 1,
                    ServiceRelationType = "hours",
                    DisplayOrder = 2,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                }
            );

            // Seed Extra Services
            modelBuilder.Entity<ExtraService>().HasData(
                new ExtraService
                {
                    Id = 1,
                    Name = "Deep Cleaning",
                    Description = "Thorough cleaning of all surfaces and hard-to-reach areas",
                    Price = 50,
                    Duration = 60,
                    Icon = "deep-cleaning.png",
                    IsDeepCleaning = true,
                    PriceMultiplier = 1.5m,
                    IsAvailableForAll = true,
                    DisplayOrder = 1,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new ExtraService
                {
                    Id = 2,
                    Name = "Super Deep Cleaning",
                    Description = "Most intensive cleaning service available",
                    Price = 100,
                    Duration = 120,
                    Icon = "super-deep-cleaning.png",
                    IsSuperDeepCleaning = true,
                    PriceMultiplier = 2.0m,
                    IsAvailableForAll = true,
                    DisplayOrder = 2,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new ExtraService
                {
                    Id = 3,
                    Name = "Same Day Service",
                    Description = "Get your cleaning done today",
                    Price = 75,
                    Duration = 0,
                    Icon = "same-day.png",
                    IsSameDayService = true,
                    IsAvailableForAll = true,
                    DisplayOrder = 3,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new ExtraService
                {
                    Id = 4,
                    Name = "Window Cleaning",
                    Description = "Interior window cleaning",
                    Price = 15,
                    Duration = 20,
                    Icon = "window-cleaning.png",
                    HasQuantity = true,
                    IsAvailableForAll = true,
                    DisplayOrder = 4,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new ExtraService
                {
                    Id = 5,
                    Name = "Wall Cleaning",
                    Description = "Spot cleaning of walls",
                    Price = 20,
                    Duration = 30,
                    Icon = "wall-cleaning.png",
                    HasQuantity = true,
                    IsAvailableForAll = true,
                    DisplayOrder = 5,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new ExtraService
                {
                    Id = 6,
                    Name = "Organizing Service",
                    Description = "Professional organizing of your space",
                    Price = 30,
                    Duration = 30,
                    Icon = "organizing.png",
                    HasHours = true,
                    IsAvailableForAll = true,
                    DisplayOrder = 6,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new ExtraService
                {
                    Id = 7,
                    Name = "Laundry Service",
                    Description = "Washing and folding service",
                    Price = 25,
                    Duration = 45,
                    Icon = "laundry.png",
                    HasQuantity = true,
                    ServiceTypeId = 1, // Only for residential
                    IsAvailableForAll = false,
                    DisplayOrder = 7,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new ExtraService
                {
                    Id = 8,
                    Name = "Refrigerator Cleaning",
                    Description = "Deep cleaning inside and outside",
                    Price = 35,
                    Duration = 30,
                    Icon = "fridge-cleaning.png",
                    IsAvailableForAll = true,
                    DisplayOrder = 8,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                },
                new ExtraService
                {
                    Id = 9,
                    Name = "Oven Cleaning",
                    Description = "Deep cleaning of oven interior",
                    Price = 40,
                    Duration = 45,
                    Icon = "oven-cleaning.png",
                    IsAvailableForAll = true,
                    DisplayOrder = 9,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                }
            );
        }
    }
}