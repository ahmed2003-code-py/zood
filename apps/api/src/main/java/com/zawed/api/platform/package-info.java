/**
 * Shared kernel: money and volume arithmetic, identifiers, time, the error contract, request
 * correlation, security defaults and configuration checks. It is an open module, so every other
 * module may use its types; it depends on no other module.
 */
@org.springframework.modulith.ApplicationModule(type = org.springframework.modulith.ApplicationModule.Type.OPEN)
package com.zawed.api.platform;
