-- Create Database (Optional, if not created yet)
CREATE DATABASE CarRentalProdDb;
GO

USE CarRentalProdDb;
GO

-- 1. Table: CarTypes (OCP Dictionary)
CREATE TABLE [dbo].[CarTypes] (
    [TypeName]    NVARCHAR (450)  NOT NULL,
    [Description] NVARCHAR (MAX)  NOT NULL,
    CONSTRAINT [PK_CarTypes] PRIMARY KEY CLUSTERED ([TypeName] ASC)
);
GO

-- 2. Table: Cars (Fleet) with RowVersion for Optimistic Concurrency Control
CREATE TABLE [dbo].[Cars] (
    [CarId]       NVARCHAR (450)  NOT NULL,
    [CarTypeName] NVARCHAR (450)  NOT NULL,
    [RowVersion]  ROWVERSION      NOT NULL,
    CONSTRAINT [PK_Cars] PRIMARY KEY CLUSTERED ([CarId] ASC),
    CONSTRAINT [FK_Cars_CarTypes_CarTypeName] FOREIGN KEY ([CarTypeName]) 
        REFERENCES [dbo].[CarTypes] ([TypeName]) ON DELETE CASCADE
);
GO

CREATE NONCLUSTERED INDEX [IX_Cars_CarTypeName] 
    ON [dbo].[Cars] ([CarTypeName] ASC);
GO

-- 3. Table: Reservations with Unique Index for Idempotency Protection
CREATE TABLE [dbo].[Reservations] (
    [ReservationId]  NVARCHAR (450)       NOT NULL,
    [CarId]          NVARCHAR (450)       NOT NULL,
    [IdempotencyKey] UNIQUEIDENTIFIER     NOT NULL,
    [StartDate]      DATETIME2 (7)        NOT NULL,
    [Days]           INT                  NOT NULL,
    [EndDate]        DATETIME2 (7)        NOT NULL,
    CONSTRAINT [PK_Reservations] PRIMARY KEY CLUSTERED ([ReservationId] ASC),
    CONSTRAINT [FK_Reservations_Cars_CarId] FOREIGN KEY ([CarId]) 
        REFERENCES [dbo].[Cars] ([CarId]) ON DELETE CASCADE
);
GO

CREATE UNIQUE NONCLUSTERED INDEX [IX_Reservations_IdempotencyKey] 
    ON [dbo].[Reservations] ([IdempotencyKey] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_Reservations_CarId] 
    ON [dbo].[Reservations] ([CarId] ASC);
GO

-- 4. Table: OutboxMessages (Reliable Asynchronous Notifications Buffer)
CREATE TABLE [dbo].[OutboxMessages] (
    [Id]            UNIQUEIDENTIFIER NOT NULL,
    [Type]          NVARCHAR (MAX)   NOT NULL,
    [Content]       NVARCHAR (MAX)   NOT NULL,
    [OccurredOnUtc] DATETIME2 (7)    NOT NULL,
    [ProcessedOnUtc] DATETIME2 (7)   NULL,
    [Error]         NVARCHAR (MAX)   NULL,
    CONSTRAINT [PK_OutboxMessages] PRIMARY KEY CLUSTERED ([Id] ASC)
);
GO

-- 5. Seed Initial Production Data
INSERT INTO [dbo].[CarTypes] ([TypeName], [Description])
VALUES 
    (N'Sedan', N'Standard passenger cars'),
    (N'SUV', N'Sport utility vehicles');
GO

INSERT INTO [dbo].[Cars] ([CarId], [CarTypeName])
VALUES 
    (N'S1', N'Sedan'),
    (N'SUV1', N'SUV');
GO
