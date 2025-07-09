CREATE TABLE IF NOT EXISTS `__EFMigrationsHistory` (
    `MigrationId` varchar(150) CHARACTER SET utf8mb4 NOT NULL,
    `ProductVersion` varchar(32) CHARACTER SET utf8mb4 NOT NULL,
    CONSTRAINT `PK___EFMigrationsHistory` PRIMARY KEY (`MigrationId`)
) CHARACTER SET=utf8mb4;

START TRANSACTION;

ALTER DATABASE CHARACTER SET utf8mb4;

CREATE TABLE `PromoCodes` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Code` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `Description` varchar(200) CHARACTER SET utf8mb4 NULL,
    `IsPercentage` tinyint(1) NOT NULL,
    `DiscountValue` decimal(18,2) NOT NULL,
    `MaxUsageCount` int NULL,
    `CurrentUsageCount` int NOT NULL,
    `MaxUsagePerUser` int NULL,
    `ValidFrom` datetime(6) NULL,
    `ValidTo` datetime(6) NULL,
    `MinimumOrderAmount` decimal(18,2) NULL,
    `IsActive` tinyint(1) NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NULL,
    CONSTRAINT `PK_PromoCodes` PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;

CREATE TABLE `ServiceTypes` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Name` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `BasePrice` decimal(18,2) NOT NULL,
    `Description` varchar(500) CHARACTER SET utf8mb4 NULL,
    `IsActive` tinyint(1) NOT NULL,
    `DisplayOrder` int NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NULL,
    CONSTRAINT `PK_ServiceTypes` PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;

CREATE TABLE `Subscriptions` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Name` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `Description` varchar(200) CHARACTER SET utf8mb4 NULL,
    `DiscountPercentage` decimal(5,2) NOT NULL,
    `SubscriptionDays` int NOT NULL,
    `IsActive` tinyint(1) NOT NULL,
    `DisplayOrder` int NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NULL,
    CONSTRAINT `PK_Subscriptions` PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;

CREATE TABLE `ExtraServices` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Name` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `Description` varchar(500) CHARACTER SET utf8mb4 NULL,
    `Price` decimal(18,2) NOT NULL,
    `Duration` int NOT NULL,
    `Icon` varchar(100) CHARACTER SET utf8mb4 NULL,
    `HasQuantity` tinyint(1) NOT NULL,
    `HasHours` tinyint(1) NOT NULL,
    `IsDeepCleaning` tinyint(1) NOT NULL,
    `IsSuperDeepCleaning` tinyint(1) NOT NULL,
    `IsSameDayService` tinyint(1) NOT NULL,
    `PriceMultiplier` decimal(18,2) NOT NULL,
    `ServiceTypeId` int NULL,
    `IsAvailableForAll` tinyint(1) NOT NULL,
    `IsActive` tinyint(1) NOT NULL,
    `DisplayOrder` int NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NULL,
    CONSTRAINT `PK_ExtraServices` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_ExtraServices_ServiceTypes_ServiceTypeId` FOREIGN KEY (`ServiceTypeId`) REFERENCES `ServiceTypes` (`Id`) ON DELETE SET NULL
) CHARACTER SET=utf8mb4;

CREATE TABLE `Services` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Name` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `ServiceKey` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `Cost` decimal(18,2) NOT NULL,
    `TimeDuration` int NOT NULL,
    `ServiceTypeId` int NOT NULL,
    `InputType` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `MinValue` int NULL,
    `MaxValue` int NULL,
    `StepValue` int NULL,
    `IsRangeInput` tinyint(1) NOT NULL,
    `Unit` varchar(20) CHARACTER SET utf8mb4 NULL,
    `ServiceRelationType` varchar(20) CHARACTER SET utf8mb4 NULL,
    `IsActive` tinyint(1) NOT NULL,
    `DisplayOrder` int NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NULL,
    CONSTRAINT `PK_Services` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_Services_ServiceTypes_ServiceTypeId` FOREIGN KEY (`ServiceTypeId`) REFERENCES `ServiceTypes` (`Id`) ON DELETE CASCADE
) CHARACTER SET=utf8mb4;

CREATE TABLE `Users` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `FirstName` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `LastName` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `Email` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `PasswordHash` longtext CHARACTER SET utf8mb4 NULL,
    `PasswordSalt` longtext CHARACTER SET utf8mb4 NULL,
    `Phone` varchar(20) CHARACTER SET utf8mb4 NULL,
    `Role` int NOT NULL,
    `RefreshToken` longtext CHARACTER SET utf8mb4 NULL,
    `RefreshTokenExpiryTime` datetime(6) NULL,
    `AuthProvider` longtext CHARACTER SET utf8mb4 NULL,
    `ExternalAuthId` longtext CHARACTER SET utf8mb4 NULL,
    `SubscriptionId` int NULL,
    `SubscriptionStartDate` datetime(6) NULL,
    `SubscriptionExpiryDate` datetime(6) NULL,
    `LastOrderDate` datetime(6) NULL,
    `FirstTimeOrder` tinyint(1) NOT NULL DEFAULT TRUE,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NULL,
    `IsActive` tinyint(1) NOT NULL,
    `IsEmailVerified` tinyint(1) NOT NULL,
    `EmailVerificationToken` longtext CHARACTER SET utf8mb4 NULL,
    `EmailVerificationTokenExpiry` datetime(6) NULL,
    `PasswordResetToken` longtext CHARACTER SET utf8mb4 NULL,
    `PasswordResetTokenExpiry` datetime(6) NULL,
    CONSTRAINT `PK_Users` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_Users_Subscriptions_SubscriptionId` FOREIGN KEY (`SubscriptionId`) REFERENCES `Subscriptions` (`Id`)
) CHARACTER SET=utf8mb4;

CREATE TABLE `Apartments` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Name` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `Address` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
    `AptSuite` varchar(50) CHARACTER SET utf8mb4 NULL,
    `City` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `State` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `PostalCode` varchar(5) CHARACTER SET utf8mb4 NOT NULL,
    `SpecialInstructions` varchar(500) CHARACTER SET utf8mb4 NULL,
    `UserId` int NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NULL,
    `IsActive` tinyint(1) NOT NULL,
    CONSTRAINT `PK_Apartments` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_Apartments_Users_UserId` FOREIGN KEY (`UserId`) REFERENCES `Users` (`Id`) ON DELETE CASCADE
) CHARACTER SET=utf8mb4;

CREATE TABLE `AuditLogs` (
    `Id` bigint NOT NULL AUTO_INCREMENT,
    `EntityType` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `EntityId` bigint NOT NULL,
    `Action` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
    `OldValues` LONGTEXT CHARACTER SET utf8mb4 NULL,
    `NewValues` LONGTEXT CHARACTER SET utf8mb4 NULL,
    `ChangedFields` LONGTEXT CHARACTER SET utf8mb4 NULL,
    `UserId` int NULL,
    `IpAddress` varchar(45) CHARACTER SET utf8mb4 NULL,
    `UserAgent` varchar(500) CHARACTER SET utf8mb4 NULL,
    `CreatedAt` datetime(6) NOT NULL,
    CONSTRAINT `PK_AuditLogs` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_AuditLogs_Users_UserId` FOREIGN KEY (`UserId`) REFERENCES `Users` (`Id`)
) CHARACTER SET=utf8mb4;

CREATE TABLE `GiftCards` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Code` varchar(14) CHARACTER SET utf8mb4 NOT NULL,
    `OriginalAmount` decimal(10,2) NOT NULL,
    `CurrentBalance` decimal(10,2) NOT NULL,
    `RecipientName` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `RecipientEmail` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `SenderName` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `SenderEmail` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `Message` varchar(500) CHARACTER SET utf8mb4 NULL,
    `IsActive` tinyint(1) NOT NULL,
    `PurchasedByUserId` int NOT NULL,
    `PaymentIntentId` varchar(100) CHARACTER SET utf8mb4 NULL,
    `IsPaid` tinyint(1) NOT NULL,
    `PaidAt` datetime(6) NULL,
    `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    `UpdatedAt` datetime(6) NULL,
    CONSTRAINT `PK_GiftCards` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_GiftCards_Users_PurchasedByUserId` FOREIGN KEY (`PurchasedByUserId`) REFERENCES `Users` (`Id`) ON DELETE RESTRICT
) CHARACTER SET=utf8mb4;

CREATE TABLE `Orders` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `UserId` int NOT NULL,
    `ApartmentId` int NULL,
    `ApartmentName` varchar(100) CHARACTER SET utf8mb4 NULL,
    `ServiceTypeId` int NOT NULL,
    `OrderDate` datetime(6) NOT NULL,
    `ServiceDate` datetime(6) NOT NULL,
    `ServiceTime` time(6) NOT NULL,
    `CancellationReason` varchar(500) CHARACTER SET utf8mb4 NULL,
    `TotalDuration` int NOT NULL,
    `MaidsCount` int NOT NULL,
    `SubTotal` decimal(18,2) NOT NULL,
    `Tax` decimal(18,2) NOT NULL,
    `Tips` decimal(18,2) NOT NULL,
    `Total` decimal(18,2) NOT NULL,
    `DiscountAmount` decimal(18,2) NOT NULL,
    `SubscriptionDiscountAmount` decimal(65,30) NOT NULL,
    `PromoCode` varchar(50) CHARACTER SET utf8mb4 NULL,
    `GiftCardAmountUsed` decimal(10,2) NOT NULL DEFAULT 0.0,
    `GiftCardCode` varchar(14) CHARACTER SET utf8mb4 NULL,
    `SubscriptionId` int NOT NULL,
    `EntryMethod` varchar(100) CHARACTER SET utf8mb4 NULL,
    `SpecialInstructions` varchar(500) CHARACTER SET utf8mb4 NULL,
    `ContactFirstName` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `ContactLastName` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `ContactEmail` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `ContactPhone` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
    `ServiceAddress` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
    `AptSuite` varchar(50) CHARACTER SET utf8mb4 NULL,
    `City` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `State` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `ZipCode` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
    `Status` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `PaymentIntentId` varchar(100) CHARACTER SET utf8mb4 NULL,
    `IsPaid` tinyint(1) NOT NULL,
    `PaidAt` datetime(6) NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NULL,
    CONSTRAINT `PK_Orders` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_Orders_Apartments_ApartmentId` FOREIGN KEY (`ApartmentId`) REFERENCES `Apartments` (`Id`) ON DELETE SET NULL,
    CONSTRAINT `FK_Orders_ServiceTypes_ServiceTypeId` FOREIGN KEY (`ServiceTypeId`) REFERENCES `ServiceTypes` (`Id`) ON DELETE RESTRICT,
    CONSTRAINT `FK_Orders_Subscriptions_SubscriptionId` FOREIGN KEY (`SubscriptionId`) REFERENCES `Subscriptions` (`Id`) ON DELETE RESTRICT,
    CONSTRAINT `FK_Orders_Users_UserId` FOREIGN KEY (`UserId`) REFERENCES `Users` (`Id`) ON DELETE RESTRICT
) CHARACTER SET=utf8mb4;

CREATE TABLE `GiftCardUsages` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `GiftCardId` int NOT NULL,
    `OrderId` int NOT NULL,
    `UserId` int NOT NULL,
    `AmountUsed` decimal(10,2) NOT NULL,
    `BalanceAfterUsage` decimal(10,2) NOT NULL,
    `UsedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT `PK_GiftCardUsages` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_GiftCardUsages_GiftCards_GiftCardId` FOREIGN KEY (`GiftCardId`) REFERENCES `GiftCards` (`Id`) ON DELETE CASCADE,
    CONSTRAINT `FK_GiftCardUsages_Orders_OrderId` FOREIGN KEY (`OrderId`) REFERENCES `Orders` (`Id`) ON DELETE RESTRICT,
    CONSTRAINT `FK_GiftCardUsages_Users_UserId` FOREIGN KEY (`UserId`) REFERENCES `Users` (`Id`) ON DELETE RESTRICT
) CHARACTER SET=utf8mb4;

CREATE TABLE `OrderExtraServices` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `OrderId` int NOT NULL,
    `ExtraServiceId` int NOT NULL,
    `Quantity` int NOT NULL,
    `Hours` decimal(65,30) NOT NULL,
    `Cost` decimal(18,2) NOT NULL,
    `Duration` int NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    CONSTRAINT `PK_OrderExtraServices` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_OrderExtraServices_ExtraServices_ExtraServiceId` FOREIGN KEY (`ExtraServiceId`) REFERENCES `ExtraServices` (`Id`) ON DELETE RESTRICT,
    CONSTRAINT `FK_OrderExtraServices_Orders_OrderId` FOREIGN KEY (`OrderId`) REFERENCES `Orders` (`Id`) ON DELETE CASCADE
) CHARACTER SET=utf8mb4;

CREATE TABLE `OrderServices` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `OrderId` int NOT NULL,
    `ServiceId` int NOT NULL,
    `Quantity` int NOT NULL,
    `Cost` decimal(18,2) NOT NULL,
    `Duration` int NOT NULL,
    `PriceMultiplier` decimal(18,2) NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    CONSTRAINT `PK_OrderServices` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_OrderServices_Orders_OrderId` FOREIGN KEY (`OrderId`) REFERENCES `Orders` (`Id`) ON DELETE CASCADE,
    CONSTRAINT `FK_OrderServices_Services_ServiceId` FOREIGN KEY (`ServiceId`) REFERENCES `Services` (`Id`) ON DELETE RESTRICT
) CHARACTER SET=utf8mb4;

INSERT INTO `ExtraServices` (`Id`, `CreatedAt`, `Description`, `DisplayOrder`, `Duration`, `HasHours`, `HasQuantity`, `Icon`, `IsActive`, `IsAvailableForAll`, `IsDeepCleaning`, `IsSameDayService`, `IsSuperDeepCleaning`, `Name`, `Price`, `PriceMultiplier`, `ServiceTypeId`, `UpdatedAt`)
VALUES (1, TIMESTAMP '2025-06-19 10:17:55', 'Thorough cleaning of all surfaces and hard-to-reach areas', 1, 60, FALSE, FALSE, 'deep-cleaning.png', TRUE, TRUE, TRUE, FALSE, FALSE, 'Deep Cleaning', 50.0, 1.5, NULL, NULL),
(2, TIMESTAMP '2025-06-19 10:17:55', 'Most intensive cleaning service available', 2, 120, FALSE, FALSE, 'super-deep-cleaning.png', TRUE, TRUE, FALSE, FALSE, TRUE, 'Super Deep Cleaning', 100.0, 2.0, NULL, NULL),
(3, TIMESTAMP '2025-06-19 10:17:55', 'Get your cleaning done today', 3, 0, FALSE, FALSE, 'same-day.png', TRUE, TRUE, FALSE, TRUE, FALSE, 'Same Day Service', 75.0, 1.0, NULL, NULL),
(4, TIMESTAMP '2025-06-19 10:17:55', 'Interior window cleaning', 4, 20, FALSE, TRUE, 'window-cleaning.png', TRUE, TRUE, FALSE, FALSE, FALSE, 'Window Cleaning', 15.0, 1.0, NULL, NULL),
(5, TIMESTAMP '2025-06-19 10:17:55', 'Spot cleaning of walls', 5, 30, FALSE, TRUE, 'wall-cleaning.png', TRUE, TRUE, FALSE, FALSE, FALSE, 'Wall Cleaning', 20.0, 1.0, NULL, NULL),
(6, TIMESTAMP '2025-06-19 10:17:55', 'Professional organizing of your space', 6, 30, TRUE, FALSE, 'organizing.png', TRUE, TRUE, FALSE, FALSE, FALSE, 'Organizing Service', 30.0, 1.0, NULL, NULL),
(8, TIMESTAMP '2025-06-19 10:17:55', 'Deep cleaning inside and outside', 8, 30, FALSE, FALSE, 'fridge-cleaning.png', TRUE, TRUE, FALSE, FALSE, FALSE, 'Refrigerator Cleaning', 35.0, 1.0, NULL, NULL),
(9, TIMESTAMP '2025-06-19 10:17:55', 'Deep cleaning of oven interior', 9, 45, FALSE, FALSE, 'oven-cleaning.png', TRUE, TRUE, FALSE, FALSE, FALSE, 'Oven Cleaning', 40.0, 1.0, NULL, NULL);

INSERT INTO `ServiceTypes` (`Id`, `BasePrice`, `CreatedAt`, `Description`, `DisplayOrder`, `IsActive`, `Name`, `UpdatedAt`)
VALUES (1, 120.0, TIMESTAMP '2025-06-19 10:17:55', 'Complete home cleaning service', 1, TRUE, 'Residential Cleaning', NULL),
(2, 200.0, TIMESTAMP '2025-06-19 10:17:55', 'Professional office cleaning service', 2, TRUE, 'Office Cleaning', NULL);

INSERT INTO `Subscriptions` (`Id`, `CreatedAt`, `Description`, `DiscountPercentage`, `DisplayOrder`, `IsActive`, `Name`, `SubscriptionDays`, `UpdatedAt`)
VALUES (1, TIMESTAMP '2025-06-19 10:17:55', 'Single cleaning service', 0.0, 1, TRUE, 'One Time', 0, NULL),
(2, TIMESTAMP '2025-06-19 10:17:55', 'Cleaning every week', 15.0, 2, TRUE, 'Weekly', 7, NULL),
(3, TIMESTAMP '2025-06-19 10:17:55', 'Cleaning every two weeks', 10.0, 3, TRUE, 'Bi-Weekly', 14, NULL),
(4, TIMESTAMP '2025-06-19 10:17:55', 'Cleaning once a month', 5.0, 4, TRUE, 'Monthly', 30, NULL);

INSERT INTO `ExtraServices` (`Id`, `CreatedAt`, `Description`, `DisplayOrder`, `Duration`, `HasHours`, `HasQuantity`, `Icon`, `IsActive`, `IsAvailableForAll`, `IsDeepCleaning`, `IsSameDayService`, `IsSuperDeepCleaning`, `Name`, `Price`, `PriceMultiplier`, `ServiceTypeId`, `UpdatedAt`)
VALUES (7, TIMESTAMP '2025-06-19 10:17:55', 'Washing and folding service', 7, 45, FALSE, TRUE, 'laundry.png', TRUE, FALSE, FALSE, FALSE, FALSE, 'Laundry Service', 25.0, 1.0, 1, NULL);

INSERT INTO `Services` (`Id`, `Cost`, `CreatedAt`, `DisplayOrder`, `InputType`, `IsActive`, `IsRangeInput`, `MaxValue`, `MinValue`, `Name`, `ServiceKey`, `ServiceRelationType`, `ServiceTypeId`, `StepValue`, `TimeDuration`, `Unit`, `UpdatedAt`)
VALUES (1, 25.0, TIMESTAMP '2025-06-19 10:17:55', 1, 'dropdown', TRUE, FALSE, 6, 0, 'Bedrooms', 'bedrooms', NULL, 1, 1, 30, NULL, NULL),
(2, 35.0, TIMESTAMP '2025-06-19 10:17:55', 2, 'dropdown', TRUE, FALSE, 5, 1, 'Bathrooms', 'bathrooms', NULL, 1, 1, 45, NULL, NULL),
(3, 0.1, TIMESTAMP '2025-06-19 10:17:55', 3, 'slider', TRUE, TRUE, 5000, 400, 'Square Feet', 'sqft', NULL, 1, 100, 1, 'per sqft', NULL),
(4, 40.0, TIMESTAMP '2025-06-19 10:17:55', 1, 'dropdown', TRUE, FALSE, 10, 1, 'Cleaners', 'cleaners', 'cleaner', 2, 1, 0, 'per hour', NULL),
(5, 0.0, TIMESTAMP '2025-06-19 10:17:55', 2, 'dropdown', TRUE, FALSE, 8, 2, 'Hours', 'hours', 'hours', 2, 1, 60, NULL, NULL);

CREATE INDEX `IX_Apartments_UserId` ON `Apartments` (`UserId`);

CREATE INDEX `IX_AuditLogs_CreatedAt` ON `AuditLogs` (`CreatedAt`);

CREATE INDEX `IX_AuditLogs_Entity` ON `AuditLogs` (`EntityType`, `EntityId`);

CREATE INDEX `IX_AuditLogs_UserId` ON `AuditLogs` (`UserId`);

CREATE INDEX `IX_ExtraServices_ServiceTypeId` ON `ExtraServices` (`ServiceTypeId`);

CREATE UNIQUE INDEX `IX_GiftCards_Code` ON `GiftCards` (`Code`);

CREATE INDEX `IX_GiftCards_PurchasedByUserId` ON `GiftCards` (`PurchasedByUserId`);

CREATE INDEX `IX_GiftCardUsages_GiftCardId` ON `GiftCardUsages` (`GiftCardId`);

CREATE INDEX `IX_GiftCardUsages_OrderId` ON `GiftCardUsages` (`OrderId`);

CREATE INDEX `IX_GiftCardUsages_UserId` ON `GiftCardUsages` (`UserId`);

CREATE INDEX `IX_OrderExtraServices_ExtraServiceId` ON `OrderExtraServices` (`ExtraServiceId`);

CREATE INDEX `IX_OrderExtraServices_OrderId` ON `OrderExtraServices` (`OrderId`);

CREATE INDEX `IX_Orders_ApartmentId` ON `Orders` (`ApartmentId`);

CREATE INDEX `IX_Orders_ServiceTypeId` ON `Orders` (`ServiceTypeId`);

CREATE INDEX `IX_Orders_SubscriptionId` ON `Orders` (`SubscriptionId`);

CREATE INDEX `IX_Orders_UserId` ON `Orders` (`UserId`);

CREATE INDEX `IX_OrderServices_OrderId` ON `OrderServices` (`OrderId`);

CREATE INDEX `IX_OrderServices_ServiceId` ON `OrderServices` (`ServiceId`);

CREATE UNIQUE INDEX `IX_PromoCodes_Code` ON `PromoCodes` (`Code`);

CREATE INDEX `IX_Services_ServiceKey` ON `Services` (`ServiceKey`);

CREATE INDEX `IX_Services_ServiceTypeId` ON `Services` (`ServiceTypeId`);

CREATE UNIQUE INDEX `IX_Users_Email` ON `Users` (`Email`);

CREATE INDEX `IX_Users_SubscriptionId` ON `Users` (`SubscriptionId`);

INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250619101755_InitialCreate', '8.0.16');

COMMIT;

