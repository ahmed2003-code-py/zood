SELECT cp, to_hex(cp) AS hex, NOT (('A' || chr(cp)) ~ '^[^[:cntrl:]]{1,40}$') AS refused
FROM unnest(ARRAY[9, 10, 13, 27, 31, 127, 128, 133, 159, 160, 173, 1564, 6158, 8203, 8204, 8205, 8206, 8207, 8232, 8233, 8234, 8238, 8288, 8294, 65279, 65529]) AS cp
ORDER BY cp;
