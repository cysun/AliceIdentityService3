SELECT 'column' AS kind, c.relname || '.' || a.attname AS key,
       jsonb_build_array(pg_catalog.format_type(a.atttypid, a.atttypmod), NOT a.attnotnull,
         pg_get_expr(d.adbin, d.adrelid), a.attidentity::text)::text AS definition
FROM pg_attribute a
JOIN pg_class c ON c.oid = a.attrelid
JOIN pg_namespace n ON n.oid = c.relnamespace
LEFT JOIN pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
WHERE n.nspname = 'public' AND c.relkind = 'r' AND a.attnum > 0 AND NOT a.attisdropped
UNION ALL
SELECT 'constraint', c.relname || '.' || k.conname, pg_get_constraintdef(k.oid, true)
FROM pg_constraint k JOIN pg_class c ON c.oid = k.conrelid JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'public'
UNION ALL
SELECT 'index', tablename || '.' || indexname, indexdef FROM pg_indexes WHERE schemaname = 'public'
UNION ALL
SELECT 'function', p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')',
       jsonb_build_array(l.lanname, pg_get_function_result(p.oid), trim(p.prosrc), pg_get_expr(p.proargdefaults, 0))::text
FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace JOIN pg_language l ON l.oid = p.prolang
WHERE n.nspname = 'public' AND p.prokind = 'f'
ORDER BY kind, key;
