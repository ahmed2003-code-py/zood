package com.zawed.api.platform.ids;

import java.util.UUID;

/** Source of new primary keys. Injected, so a test can supply known ids. */
public interface IdGenerator {

	UUID next();

}
