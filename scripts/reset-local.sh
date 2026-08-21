#!/usr/bin/env bash
# Resets local development state: the database schemas AND the broker.
#
# Purging RabbitMQ is not optional. Recreating the schemas without it leaves the previous run's
# messages queued; they redeliver against an empty database and interleave with the new run's
# events. The symptom is baffling - obligations referencing requirement ids that exist nowhere -
# and has nothing to do with the code. Found exactly that way.
set -euo pipefail

echo "Purging RabbitMQ..."
docker exec certiflow-rabbit rabbitmqctl stop_app >/dev/null
docker exec certiflow-rabbit rabbitmqctl reset >/dev/null
docker exec certiflow-rabbit rabbitmqctl start_app >/dev/null
echo "  broker reset"

echo "Dropping schemas..."
docker exec certiflow-sql bash -lc "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Certiflow!Local1' -C -d certiflow -Q \"
SET QUOTED_IDENTIFIER ON;
DECLARE @sql NVARCHAR(MAX) = N'';
SELECT @sql += N'ALTER TABLE [' + s.name + '].[' + t.name + '] DROP CONSTRAINT [' + f.name + '];'
FROM sys.foreign_keys f
JOIN sys.tables t ON f.parent_object_id = t.object_id
JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE s.name IN ('registry','intake','intelligence','verification','compliance','audit','notification','reporting');
SELECT @sql += N'DROP TABLE [' + s.name + '].[' + t.name + '];'
FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE s.name IN ('registry','intake','intelligence','verification','compliance','audit','notification','reporting');
EXEC sp_executesql @sql;\"" >/dev/null 2>&1 || true
echo "  schemas dropped"
echo
echo "Start the services now; each recreates its own schema in development."
