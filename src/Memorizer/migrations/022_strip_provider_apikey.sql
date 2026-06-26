-- 022_strip_provider_apikey.sql: Remove plaintext provider API keys from persisted provider settings.
-- API keys are intentionally sourced from environment/application configuration instead.

CREATE OR REPLACE FUNCTION memorizer_strip_provider_config_secrets(input_value jsonb)
RETURNS jsonb
LANGUAGE plpgsql
AS $$
DECLARE
    item RECORD;
    normalized_key TEXT;
    result JSONB;
BEGIN
    CASE jsonb_typeof(input_value)
        WHEN 'object' THEN
            result := '{}'::jsonb;

            FOR item IN SELECT key, value FROM jsonb_each(input_value)
            LOOP
                normalized_key := lower(replace(replace(item.key, '_', ''), '-', ''));

                IF normalized_key IN ('apikey', 'token', 'accesstoken', 'secret', 'password', 'authorization')
                   OR normalized_key LIKE '%apikey'
                   OR normalized_key LIKE '%token'
                   OR normalized_key LIKE '%secret'
                   OR normalized_key LIKE '%password'
                THEN
                    CONTINUE;
                END IF;

                result := result || jsonb_build_object(
                    item.key,
                    memorizer_strip_provider_config_secrets(item.value));
            END LOOP;

            RETURN result;

        WHEN 'array' THEN
            SELECT COALESCE(
                jsonb_agg(memorizer_strip_provider_config_secrets(value)),
                '[]'::jsonb)
            INTO result
            FROM jsonb_array_elements(input_value);

            RETURN result;

        ELSE
            RETURN input_value;
    END CASE;
END;
$$;

UPDATE provider_settings
SET config = memorizer_strip_provider_config_secrets(config)
WHERE config <> memorizer_strip_provider_config_secrets(config);

DROP FUNCTION memorizer_strip_provider_config_secrets(jsonb);
