package com.zawed.api;

import static org.assertj.core.api.Assertions.assertThat;

import java.util.List;
import java.util.Map;
import java.util.stream.Collectors;

import com.tngtech.archunit.core.domain.JavaClass;
import com.tngtech.archunit.core.importer.ClassFileImporter;
import com.tngtech.archunit.core.importer.ImportOption;

import org.junit.jupiter.api.Test;

/**
 * springdoc names an OpenAPI schema after the record's simple name. Two request or response
 * records with the same simple name collapse into one schema, and the published contract silently
 * describes only one of them: B2's company {@code StatusRequest} once replaced the user one, so the
 * web's generated types offered company statuses for a user. Every API record needs its own name.
 */
class ApiSchemaNameTests {

	@Test
	void everyApiRecordHasItsOwnSimpleName() {
		List<JavaClass> apiRecords = new ClassFileImporter().withImportOption(ImportOption.Predefined.DO_NOT_INCLUDE_TESTS)
			.importPackages("com.zawed.api")
			.stream()
			.filter((type) -> type.reflect().isRecord())
			.filter(ApiSchemaNameTests::isApiType)
			.toList();

		Map<String, List<String>> byName = apiRecords.stream()
			.collect(Collectors.groupingBy(JavaClass::getSimpleName,
					Collectors.mapping(JavaClass::getName, Collectors.toList())));
		Map<String, List<String>> duplicates = byName.entrySet()
			.stream()
			.filter((entry) -> entry.getValue().size() > 1)
			.collect(Collectors.toMap(Map.Entry::getKey, Map.Entry::getValue));

		assertThat(apiRecords).as("the scan found the API records").hasSizeGreaterThan(20);
		assertThat(duplicates).as("API records sharing a simple name (one schema would hide the other)").isEmpty();
	}

	/** Records declared in a controller or in a module's request/response holder (…Api). */
	private static boolean isApiType(JavaClass type) {
		return type.getEnclosingClass()
			.map((outer) -> outer.getSimpleName().endsWith("Controller") || outer.getSimpleName().endsWith("Api"))
			.orElse(false);
	}

}
