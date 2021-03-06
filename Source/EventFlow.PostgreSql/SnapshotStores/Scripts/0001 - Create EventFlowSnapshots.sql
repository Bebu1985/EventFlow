﻿CREATE TABLE  IF NOT EXISTS EventFlowSnapshots(
	Id bigint  GENERATED BY DEFAULT AS IDENTITY NOT NULL,
	AggregateId varchar(128) NOT NULL,
	AggregateName varchar(128) NOT NULL,
	AggregateSequenceNumber int NOT NULL,
	Data text NOT NULL,
	Metadata text NOT NULL,
	CONSTRAINT "PK_EventFlowSnapshots" PRIMARY KEY
	(
		Id
	)
);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'IX_EventFlowSnapshots_AggregateId_AggregateSequenceNumber') THEN
		CREATE TYPE "IX_EventFlowSnapshots_AggregateId_AggregateSequenceNumber" AS
		(
			AggregateName varchar(255),
			AggregateId varchar(255),
			AggregateSequenceNumber int
		);
    END IF;
END
$$;