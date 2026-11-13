/**
 * The fueling rules (plan §2: "pure fueling validation, no Spring, no database"). The engine (B4)
 * and offline ingest (B5) gather facts under their locks and ask this module for the decision, so
 * the rules have one implementation, pinned by golden tests ported from the demo.
 */
@org.springframework.modulith.ApplicationModule(type = org.springframework.modulith.ApplicationModule.Type.OPEN)
package com.zawed.api.rules;
