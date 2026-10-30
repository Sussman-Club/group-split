namespace GroupSplit.Data.PostgreSQL;

/// <summary>Payload-free, transactional wakeups for the durable job queue.</summary>
public static class BackgroundJobNotifications
{
    public const string Channel = "groupsplit_background_jobs";

    public const string CreateSql = """
        CREATE FUNCTION "NotifyBackgroundJobAvailable"() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            PERFORM pg_notify('groupsplit_background_jobs', '');
            RETURN NEW;
        END;
        $$;

        CREATE TRIGGER "BackgroundJobInserted"
        AFTER INSERT ON "BackgroundJob"
        FOR EACH ROW WHEN (NEW."Status" = 'Pending')
        EXECUTE FUNCTION "NotifyBackgroundJobAvailable"();

        CREATE TRIGGER "BackgroundJobRescheduled"
        AFTER UPDATE OF "Status", "AvailableAt" ON "BackgroundJob"
        FOR EACH ROW WHEN (NEW."Status" = 'Pending' AND
            (OLD."Status" IS DISTINCT FROM NEW."Status" OR
             OLD."AvailableAt" IS DISTINCT FROM NEW."AvailableAt"))
        EXECUTE FUNCTION "NotifyBackgroundJobAvailable"();
        """;

    public const string DropSql = """
        DROP TRIGGER IF EXISTS "BackgroundJobInserted" ON "BackgroundJob";
        DROP TRIGGER IF EXISTS "BackgroundJobRescheduled" ON "BackgroundJob";
        DROP FUNCTION IF EXISTS "NotifyBackgroundJobAvailable"();
        """;
}
