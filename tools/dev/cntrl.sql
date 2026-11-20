SELECT ('TRK' || chr(9) || '001') ~ '^[^[:cntrl:]]{1,40}$' AS tab_passes,
       ('BAD' || chr(1)) ~ '^[^[:cntrl:]]{1,40}$' AS x01_passes,
       ('A' || chr(127)) ~ '^[^[:cntrl:]]{1,40}$' AS del_passes,
       ('A' || chr(133)) ~ '^[^[:cntrl:]]{1,40}$' AS c1_nel_passes,
       (SELECT datcollate || ' / ' || datctype || ' / ' || datlocprovider::text FROM pg_database WHERE datname = current_database()) AS locale;
