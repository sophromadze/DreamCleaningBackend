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

START TRANSACTION;

CREATE TABLE `SpecialOffers` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Name` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `Description` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
    `IsPercentage` tinyint(1) NOT NULL,
    `DiscountValue` decimal(18,2) NOT NULL,
    `Type` int NOT NULL,
    `ValidFrom` datetime(6) NULL,
    `ValidTo` datetime(6) NULL,
    `Icon` longtext CHARACTER SET utf8mb4 NULL,
    `BadgeColor` longtext CHARACTER SET utf8mb4 NULL,
    `DisplayOrder` int NOT NULL,
    `MinimumOrderAmount` decimal(18,2) NULL,
    `IsActive` tinyint(1) NOT NULL,
    `RequiresFirstTimeCustomer` tinyint(1) NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NULL,
    `CreatedByUserId` int NULL,
    CONSTRAINT `PK_SpecialOffers` PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;

CREATE TABLE `UserSpecialOffers` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `UserId` int NOT NULL,
    `SpecialOfferId` int NOT NULL,
    `IsUsed` tinyint(1) NOT NULL,
    `UsedAt` datetime(6) NULL,
    `UsedOnOrderId` int NULL,
    `GrantedAt` datetime(6) NOT NULL,
    `ExpiresAt` datetime(6) NULL,
    CONSTRAINT `PK_UserSpecialOffers` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_UserSpecialOffers_Orders_UsedOnOrderId` FOREIGN KEY (`UsedOnOrderId`) REFERENCES `Orders` (`Id`),
    CONSTRAINT `FK_UserSpecialOffers_SpecialOffers_SpecialOfferId` FOREIGN KEY (`SpecialOfferId`) REFERENCES `SpecialOffers` (`Id`) ON DELETE CASCADE,
    CONSTRAINT `FK_UserSpecialOffers_Users_UserId` FOREIGN KEY (`UserId`) REFERENCES `Users` (`Id`) ON DELETE CASCADE
) CHARACTER SET=utf8mb4;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-21 17:17:57'
WHERE `Id` = 4;
SELECT ROW_COUNT();


CREATE INDEX `IX_UserSpecialOffers_SpecialOfferId` ON `UserSpecialOffers` (`SpecialOfferId`);

CREATE INDEX `IX_UserSpecialOffers_UsedOnOrderId` ON `UserSpecialOffers` (`UsedOnOrderId`);

CREATE UNIQUE INDEX `IX_UserSpecialOffers_UserId_SpecialOfferId` ON `UserSpecialOffers` (`UserId`, `SpecialOfferId`);

INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250621131758_AddSpecialOffersSystem', '8.0.16');

COMMIT;

START TRANSACTION;

ALTER TABLE `UserSpecialOffers` DROP FOREIGN KEY `FK_UserSpecialOffers_Orders_UsedOnOrderId`;

ALTER TABLE `UserSpecialOffers` DROP INDEX `IX_UserSpecialOffers_UsedOnOrderId`;

ALTER TABLE `UserSpecialOffers` ADD `UsedOnOrderId1` int NULL;

ALTER TABLE `Orders` ADD `SpecialOfferName` varchar(100) CHARACTER SET utf8mb4 NULL;

ALTER TABLE `Orders` ADD `UserSpecialOfferId` int NULL;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-22 16:44:22'
WHERE `Id` = 4;
SELECT ROW_COUNT();


CREATE INDEX `IX_UserSpecialOffers_UsedOnOrderId1` ON `UserSpecialOffers` (`UsedOnOrderId1`);

ALTER TABLE `UserSpecialOffers` ADD CONSTRAINT `FK_UserSpecialOffers_Orders_UsedOnOrderId1` FOREIGN KEY (`UsedOnOrderId1`) REFERENCES `Orders` (`Id`);

INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250622124423_AddSpecialOfferTrackingToOrders', '8.0.16');

COMMIT;

START TRANSACTION;

ALTER TABLE `UserSpecialOffers` DROP FOREIGN KEY `FK_UserSpecialOffers_Orders_UsedOnOrderId1`;

ALTER TABLE `UserSpecialOffers` DROP INDEX `IX_UserSpecialOffers_UsedOnOrderId1`;

ALTER TABLE `UserSpecialOffers` DROP COLUMN `UsedOnOrderId1`;

CREATE TABLE `GiftCardConfigs` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `BackgroundImagePath` longtext CHARACTER SET utf8mb4 NOT NULL,
    `LastUpdated` datetime(6) NULL,
    CONSTRAINT `PK_GiftCardConfigs` PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-24 02:23:46'
WHERE `Id` = 4;
SELECT ROW_COUNT();


CREATE INDEX `IX_UserSpecialOffers_UsedOnOrderId` ON `UserSpecialOffers` (`UsedOnOrderId`);

ALTER TABLE `UserSpecialOffers` ADD CONSTRAINT `FK_UserSpecialOffers_Orders_UsedOnOrderId` FOREIGN KEY (`UsedOnOrderId`) REFERENCES `Orders` (`Id`) ON DELETE SET NULL;

INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250623222346_AddChangeBackgroundForGiftCards', '8.0.16');

COMMIT;

START TRANSACTION;

ALTER TABLE `Orders` ADD `CompanyDevelopmentTips` decimal(18,2) NOT NULL DEFAULT 0.0;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-26 18:42:51'
WHERE `Id` = 4;
SELECT ROW_COUNT();


INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250626144252_AddCompanyDevelopmentTipsToOrders', '8.0.16');

COMMIT;

START TRANSACTION;

ALTER TABLE `ServiceTypes` ADD `TimeDuration` int NOT NULL DEFAULT 0;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26', `TimeDuration` = 90
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26', `TimeDuration` = 120
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-26 21:45:26'
WHERE `Id` = 4;
SELECT ROW_COUNT();


INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250626174526_AddTimeDurationToServiceType', '8.0.16');

COMMIT;

START TRANSACTION;

CREATE TABLE `NotificationLogs` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `OrderId` int NOT NULL,
    `CleanerId` int NOT NULL,
    `NotificationType` longtext CHARACTER SET utf8mb4 NOT NULL,
    `SentAt` datetime(6) NOT NULL,
    CONSTRAINT `PK_NotificationLogs` PRIMARY KEY (`Id`)
) CHARACTER SET=utf8mb4;

CREATE TABLE `OrderCleaners` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `OrderId` int NOT NULL,
    `CleanerId` int NOT NULL,
    `AssignedAt` datetime(6) NOT NULL,
    `AssignedBy` int NOT NULL,
    `TipsForCleaner` varchar(1000) CHARACTER SET utf8mb4 NULL,
    CONSTRAINT `PK_OrderCleaners` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_OrderCleaners_Orders_OrderId` FOREIGN KEY (`OrderId`) REFERENCES `Orders` (`Id`) ON DELETE CASCADE,
    CONSTRAINT `FK_OrderCleaners_Users_AssignedBy` FOREIGN KEY (`AssignedBy`) REFERENCES `Users` (`Id`) ON DELETE RESTRICT,
    CONSTRAINT `FK_OrderCleaners_Users_CleanerId` FOREIGN KEY (`CleanerId`) REFERENCES `Users` (`Id`) ON DELETE RESTRICT
) CHARACTER SET=utf8mb4;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-06-30 16:13:33'
WHERE `Id` = 4;
SELECT ROW_COUNT();


CREATE INDEX `IX_OrderCleaners_AssignedBy` ON `OrderCleaners` (`AssignedBy`);

CREATE INDEX `IX_OrderCleaners_CleanerId` ON `OrderCleaners` (`CleanerId`);

CREATE INDEX `IX_OrderCleaners_OrderId` ON `OrderCleaners` (`OrderId`);

INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250630121334_AddCleanerManagementSystem', '8.0.16');

COMMIT;

START TRANSACTION;

ALTER TABLE `Users` ADD `EmailChangeToken` longtext CHARACTER SET utf8mb4 NULL;

ALTER TABLE `Users` ADD `EmailChangeTokenExpiry` datetime(6) NULL;

ALTER TABLE `Users` ADD `PendingEmail` longtext CHARACTER SET utf8mb4 NULL;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-01 22:59:08'
WHERE `Id` = 4;
SELECT ROW_COUNT();


INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250701185909_AddEmailChangeFields', '8.0.16');

COMMIT;

START TRANSACTION;

ALTER TABLE `NotificationLogs` MODIFY COLUMN `CleanerId` int NULL;

ALTER TABLE `NotificationLogs` ADD `CustomerId` int NULL;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-02 00:40:19'
WHERE `Id` = 4;
SELECT ROW_COUNT();


CREATE INDEX `IX_NotificationLogs_CleanerId` ON `NotificationLogs` (`CleanerId`);

CREATE INDEX `IX_NotificationLogs_CustomerId` ON `NotificationLogs` (`CustomerId`);

CREATE INDEX `IX_NotificationLogs_OrderId` ON `NotificationLogs` (`OrderId`);

ALTER TABLE `NotificationLogs` ADD CONSTRAINT `FK_NotificationLogs_Orders_OrderId` FOREIGN KEY (`OrderId`) REFERENCES `Orders` (`Id`) ON DELETE CASCADE;

ALTER TABLE `NotificationLogs` ADD CONSTRAINT `FK_NotificationLogs_Users_CleanerId` FOREIGN KEY (`CleanerId`) REFERENCES `Users` (`Id`);

ALTER TABLE `NotificationLogs` ADD CONSTRAINT `FK_NotificationLogs_Users_CustomerId` FOREIGN KEY (`CustomerId`) REFERENCES `Users` (`Id`);

INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250701204020_AddCustomerIdToNotificationLog', '8.0.16');

COMMIT;

START TRANSACTION;

ALTER TABLE `ExtraServices` ADD `HasCondition` tinyint(1) NOT NULL DEFAULT FALSE;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32', `HasCondition` = FALSE
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32', `HasCondition` = FALSE
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32', `HasCondition` = FALSE
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32', `HasCondition` = FALSE
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32', `HasCondition` = FALSE
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32', `HasCondition` = FALSE
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32', `HasCondition` = FALSE
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32', `HasCondition` = FALSE
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32', `HasCondition` = FALSE
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:12:32'
WHERE `Id` = 4;
SELECT ROW_COUNT();


INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250703181233_ddasonditionoxtraervice', '8.0.16');

COMMIT;

START TRANSACTION;

ALTER TABLE `OrderExtraServices` ADD `Condition` int NULL;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-03 22:19:37'
WHERE `Id` = 4;
SELECT ROW_COUNT();


INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250703181938_AddConditionToExtraServicesAndOrders', '8.0.16');

COMMIT;

START TRANSACTION;

ALTER TABLE `OrderExtraServices` DROP COLUMN `Condition`;

ALTER TABLE `ExtraServices` DROP COLUMN `HasCondition`;

ALTER TABLE `ServiceTypes` ADD `HasPoll` tinyint(1) NOT NULL DEFAULT FALSE;

CREATE TABLE `PollQuestions` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `Question` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
    `QuestionType` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `Options` varchar(1000) CHARACTER SET utf8mb4 NULL,
    `IsRequired` tinyint(1) NOT NULL,
    `DisplayOrder` int NOT NULL,
    `IsActive` tinyint(1) NOT NULL,
    `ServiceTypeId` int NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NULL,
    CONSTRAINT `PK_PollQuestions` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_PollQuestions_ServiceTypes_ServiceTypeId` FOREIGN KEY (`ServiceTypeId`) REFERENCES `ServiceTypes` (`Id`) ON DELETE CASCADE
) CHARACTER SET=utf8mb4;

CREATE TABLE `PollSubmissions` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `UserId` int NOT NULL,
    `ServiceTypeId` int NOT NULL,
    `ContactFirstName` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `ContactLastName` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `ContactEmail` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
    `ContactPhone` varchar(20) CHARACTER SET utf8mb4 NULL,
    `ServiceAddress` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
    `AptSuite` varchar(50) CHARACTER SET utf8mb4 NULL,
    `City` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `State` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `PostalCode` varchar(10) CHARACTER SET utf8mb4 NOT NULL,
    `Status` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `AdminNotes` varchar(1000) CHARACTER SET utf8mb4 NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NULL,
    CONSTRAINT `PK_PollSubmissions` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_PollSubmissions_ServiceTypes_ServiceTypeId` FOREIGN KEY (`ServiceTypeId`) REFERENCES `ServiceTypes` (`Id`) ON DELETE RESTRICT,
    CONSTRAINT `FK_PollSubmissions_Users_UserId` FOREIGN KEY (`UserId`) REFERENCES `Users` (`Id`) ON DELETE RESTRICT
) CHARACTER SET=utf8mb4;

CREATE TABLE `PollAnswers` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `PollSubmissionId` int NOT NULL,
    `PollQuestionId` int NOT NULL,
    `Answer` varchar(2000) CHARACTER SET utf8mb4 NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    CONSTRAINT `PK_PollAnswers` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_PollAnswers_PollQuestions_PollQuestionId` FOREIGN KEY (`PollQuestionId`) REFERENCES `PollQuestions` (`Id`) ON DELETE RESTRICT,
    CONSTRAINT `FK_PollAnswers_PollSubmissions_PollSubmissionId` FOREIGN KEY (`PollSubmissionId`) REFERENCES `PollSubmissions` (`Id`) ON DELETE CASCADE
) CHARACTER SET=utf8mb4;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47', `HasPoll` = FALSE
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47', `HasPoll` = FALSE
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-05 20:31:47'
WHERE `Id` = 4;
SELECT ROW_COUNT();


CREATE INDEX `IX_PollAnswers_PollQuestionId` ON `PollAnswers` (`PollQuestionId`);

CREATE INDEX `IX_PollAnswers_PollSubmissionId` ON `PollAnswers` (`PollSubmissionId`);

CREATE INDEX `IX_PollQuestions_ServiceTypeId` ON `PollQuestions` (`ServiceTypeId`);

CREATE INDEX `IX_PollSubmissions_ServiceTypeId` ON `PollSubmissions` (`ServiceTypeId`);

CREATE INDEX `IX_PollSubmissions_UserId` ON `PollSubmissions` (`UserId`);

INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250705163148_AddPollFunctionality', '8.0.16');

COMMIT;

START TRANSACTION;

ALTER TABLE `PollSubmissions` MODIFY COLUMN `UserId` int NULL;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-05 22:16:44'
WHERE `Id` = 4;
SELECT ROW_COUNT();


INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250705181645_MakeUserIdNullableInPollSubmissions', '8.0.16');

COMMIT;

START TRANSACTION;

ALTER TABLE `ServiceTypes` ADD `IsCustom` tinyint(1) NOT NULL DEFAULT FALSE;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46', `IsCustom` = FALSE
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46', `IsCustom` = FALSE
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-06 00:44:46'
WHERE `Id` = 4;
SELECT ROW_COUNT();


INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250705204447_AddCustomFunctionality', '8.0.16');

COMMIT;

START TRANSACTION;

ALTER TABLE `ServiceTypes` MODIFY COLUMN `TimeDuration` decimal(65,30) NOT NULL;

ALTER TABLE `Services` MODIFY COLUMN `TimeDuration` decimal(18,2) NOT NULL;

ALTER TABLE `OrderServices` MODIFY COLUMN `Duration` decimal(65,30) NOT NULL;

ALTER TABLE `Orders` MODIFY COLUMN `TotalDuration` decimal(65,30) NOT NULL;

ALTER TABLE `OrderExtraServices` MODIFY COLUMN `Duration` decimal(65,30) NOT NULL;

ALTER TABLE `ExtraServices` MODIFY COLUMN `Duration` decimal(65,30) NOT NULL;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21', `Duration` = 60.0
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21', `Duration` = 120.0
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21', `Duration` = 0.0
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21', `Duration` = 20.0
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21', `Duration` = 30.0
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21', `Duration` = 30.0
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21', `Duration` = 45.0
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21', `Duration` = 30.0
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21', `Duration` = 45.0
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21', `TimeDuration` = 90.0
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21', `TimeDuration` = 120.0
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21', `TimeDuration` = 30.0
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21', `TimeDuration` = 45.0
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21', `TimeDuration` = 1.0
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21', `TimeDuration` = 0.0
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21', `TimeDuration` = 60.0
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-12 01:47:21'
WHERE `Id` = 4;
SELECT ROW_COUNT();


INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250711214722_ChangeTimeDurationToDecimal', '8.0.16');

COMMIT;

START TRANSACTION;

ALTER TABLE `Users` ADD `ProfilePictureUrl` varchar(500) CHARACTER SET utf8mb4 NULL;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-12 20:17:03'
WHERE `Id` = 4;
SELECT ROW_COUNT();


INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250712161704_AddProfilePictureToUser', '8.0.16');

COMMIT;

START TRANSACTION;

ALTER TABLE `Orders` ADD `IsAuthorizationOnly` tinyint(1) NOT NULL DEFAULT FALSE;

ALTER TABLE `Orders` ADD `PaymentCapturedAt` datetime(6) NULL;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-14 23:00:13'
WHERE `Id` = 4;
SELECT ROW_COUNT();


INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250714190014_AddAuthorizationTrackingToOrders', '8.0.16');

COMMIT;

START TRANSACTION;

ALTER TABLE `Orders` ADD `AdditionalPaymentIntentId` varchar(100) CHARACTER SET utf8mb4 NULL;

ALTER TABLE `Orders` ADD `HasAdditionalPayment` tinyint(1) NOT NULL DEFAULT FALSE;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-16 01:53:15'
WHERE `Id` = 4;
SELECT ROW_COUNT();


INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250715215316_AddAditionalPaymentConfirmation', '8.0.16');

COMMIT;

START TRANSACTION;

ALTER TABLE `Orders` DROP COLUMN `AdditionalPaymentIntentId`;

ALTER TABLE `Orders` DROP COLUMN `HasAdditionalPayment`;

CREATE TABLE `PaymentHistories` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `OrderId` int NOT NULL,
    `PaymentIntentId` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `PaymentType` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `Amount` decimal(10,2) NOT NULL,
    `Description` varchar(500) CHARACTER SET utf8mb4 NULL,
    `PaymentDate` datetime(6) NOT NULL,
    CONSTRAINT `PK_PaymentHistories` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_PaymentHistories_Orders_OrderId` FOREIGN KEY (`OrderId`) REFERENCES `Orders` (`Id`) ON DELETE CASCADE
) CHARACTER SET=utf8mb4;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-16 03:57:34'
WHERE `Id` = 4;
SELECT ROW_COUNT();


CREATE INDEX `IX_PaymentHistories_OrderId` ON `PaymentHistories` (`OrderId`);

INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250715235735_AddPaymentHistory', '8.0.16');

COMMIT;

START TRANSACTION;

DROP TABLE `PaymentHistories`;

ALTER TABLE `Orders` ADD `InitialCompanyDevelopmentTips` decimal(18,2) NOT NULL DEFAULT 0.0;

ALTER TABLE `Orders` ADD `InitialSubTotal` decimal(18,2) NOT NULL DEFAULT 0.0;

ALTER TABLE `Orders` ADD `InitialTax` decimal(18,2) NOT NULL DEFAULT 0.0;

ALTER TABLE `Orders` ADD `InitialTips` decimal(18,2) NOT NULL DEFAULT 0.0;

ALTER TABLE `Orders` ADD `InitialTotal` decimal(18,2) NOT NULL DEFAULT 0.0;

CREATE TABLE `OrderUpdateHistories` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `OrderId` int NOT NULL,
    `UpdatedByUserId` int NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    `OriginalSubTotal` decimal(18,2) NOT NULL,
    `OriginalTax` decimal(18,2) NOT NULL,
    `OriginalTips` decimal(18,2) NOT NULL,
    `OriginalCompanyDevelopmentTips` decimal(18,2) NOT NULL,
    `OriginalTotal` decimal(18,2) NOT NULL,
    `NewSubTotal` decimal(18,2) NOT NULL,
    `NewTax` decimal(18,2) NOT NULL,
    `NewTips` decimal(18,2) NOT NULL,
    `NewCompanyDevelopmentTips` decimal(18,2) NOT NULL,
    `NewTotal` decimal(18,2) NOT NULL,
    `AdditionalAmount` decimal(18,2) NOT NULL,
    `PaymentIntentId` varchar(100) CHARACTER SET utf8mb4 NULL,
    `UpdateNotes` varchar(500) CHARACTER SET utf8mb4 NULL,
    `IsPaid` tinyint(1) NOT NULL DEFAULT FALSE,
    `PaidAt` datetime(6) NULL,
    CONSTRAINT `PK_OrderUpdateHistories` PRIMARY KEY (`Id`),
    CONSTRAINT `FK_OrderUpdateHistories_Orders_OrderId` FOREIGN KEY (`OrderId`) REFERENCES `Orders` (`Id`) ON DELETE CASCADE,
    CONSTRAINT `FK_OrderUpdateHistories_Users_UpdatedByUserId` FOREIGN KEY (`UpdatedByUserId`) REFERENCES `Users` (`Id`) ON DELETE RESTRICT
) CHARACTER SET=utf8mb4;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-17 01:34:37'
WHERE `Id` = 4;
SELECT ROW_COUNT();


CREATE INDEX `IX_OrderUpdateHistories_OrderId` ON `OrderUpdateHistories` (`OrderId`);

CREATE INDEX `IX_OrderUpdateHistories_UpdatedByUserId` ON `OrderUpdateHistories` (`UpdatedByUserId`);

INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250716213438_AddOrderHistory', '8.0.16');

COMMIT;

START TRANSACTION;

UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 6;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 7;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 8;
SELECT ROW_COUNT();


UPDATE `ExtraServices` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 9;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `ServiceTypes` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 4;
SELECT ROW_COUNT();


UPDATE `Services` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 5;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 1;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 2;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 3;
SELECT ROW_COUNT();


UPDATE `Subscriptions` SET `CreatedAt` = TIMESTAMP '2025-07-17 08:54:59'
WHERE `Id` = 4;
SELECT ROW_COUNT();


INSERT INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20250717045500_AddNewDatabaseForHost', '8.0.16');

COMMIT;

