# Makefile for the MDPlayer FMP visualization / renderer build.
#
# Targets:
#   make            - build Release (incl. native libs) and run the fast
#                     unit-test suite (default)
#   make build      - build Release
#   make native     - build the native SPC/OPNA libraries via cmake
#   make debug      - build Debug
#   make test       - run all three test projects (Release) and build
#   make test-quick - build+run ONLY the fast unit-test project (no CLI/GUI
#                     integration, no heavy Avalonia build) ~ seconds
#   make test-full  - alias for the full test suite (same as `make test`)
#   make test-one   - run a single test by --filter (TEST_FILTER=...)
#   make clean      - remove obj/ and bin/ for the FMP projects
#   make purge      - clean the whole solution (including submodule deps)
#
# `build` and `test` first build the native SPC/OPNA libraries (mdplayer_spc /
# mdplayer_opna) with cmake; their post-build steps copy the result into
# MDPlayer/runtimes/<rid>/native, which the CLI and GUI projects package into
# their own output. Without this, the UI reports that SPC/OPNA playback
# requires the native library.
#
# The CLI entry point after a build is:
#   MDPlayer/src/MDPlayer.Fmp.Cli/bin/Release/net8.0/mdplayer-render
# (the repo-root ./fmp-render wrapper also resolves it).

SLN       := MDPlayer/MDPlayer.Fmp.sln
CONFIG    ?= Release
FRAMEWORK ?= net8.0
CLI_DLL   := MDPlayer/src/MDPlayer.Fmp.Cli/bin/$(CONFIG)/$(FRAMEWORK)/mdplayer-render.dll

# Native libraries built with cmake (Linux). Their CMake post-build steps copy
# the built library into MDPlayer/runtimes/<rid>/native automatically.
NATIVE_PROJECTS := \
	MDPlayer/native/MDPlayer.SpcNative \
	MDPlayer/native/MDPlayer.OpnaNative
NATIVE_SRC_DIR  := $(firstword $(NATIVE_PROJECTS))
NATIVE_RUNTIME_RID ?= linux-x64

TEST_PROJECTS := \
	MDPlayer/tests/MDPlayer.Fmp.Tests/MDPlayer.Fmp.Tests.csproj \
	MDPlayer/tests/MDPlayer.Fmp.Application.Tests/MDPlayer.Fmp.Application.Tests.csproj \
	MDPlayer/tests/MDPlayer.Fmp.Gui.Tests/MDPlayer.Fmp.Gui.Tests.csproj

# Fast path: only the pure unit-test project (references just the Application
# layer; no CLI/renderers, no heavy Avalonia GUI build). This is what
# `make test-quick` builds and runs.
QUICK_TEST_PROJECT := MDPlayer/tests/MDPlayer.Fmp.Application.Tests/MDPlayer.Fmp.Application.Tests.csproj

DOTNET ?= dotnet

.PHONY: all build debug release native test test-quick test-full test-one clean purge cli

all: build test-quick

build release: native
	$(DOTNET) build $(SLN) -c $(CONFIG)

# Build the native SPC/OPNA libraries with cmake. Configure once per build
# type, then build incrementally; the CMake post-build step copies each library
# into MDPlayer/runtimes/<rid>/native for the .NET projects to package.
native:
	@command -v cmake >/dev/null 2>&1 || { echo "cmake is required to build the native libraries" 1>&2; exit 2; }
	@for d in $(NATIVE_PROJECTS); do \
		echo "==> cmake configure $$d"; \
		cmake -S $$d -B $$d/build -DCMAKE_BUILD_TYPE=$(CONFIG) || exit $$?; \
		echo "==> cmake --build $$d"; \
		cmake --build $$d/build || exit $$?; \
	done
	@echo "native libraries ready in MDPlayer/runtimes/$(NATIVE_RUNTIME_RID)/native/"

debug:
	$(MAKE) build CONFIG=Debug

cli:
	$(DOTNET) build MDPlayer/src/MDPlayer.Fmp.Cli/MDPlayer.Fmp.Cli.csproj -c $(CONFIG)
	@echo "CLI: $${PWD}/$(CLI_DLL)"

# Run each test project in $1 (space-separated paths) once, then report a
# summary. Each project is run even if an earlier one fails (some il-reflection
# tests are flaky), and make exits nonzero only if at least one project
# reported failing tests. Consumed via $(call run_tests,<projects>).
define run_tests
	@for p in $(1); do \
		echo "==> dotnet test $$p"; \
		$(DOTNET) test $$p -c $(CONFIG) --no-build; \
		rc=$$?; \
		if [ $$rc -ne 0 ]; then FAILED=1; echo "*** FAILED: $$p"; else echo "*** OK: $$p"; fi; \
	done; \
	if [ "$$FAILED" = "1" ]; then echo "### one or more test projects failed"; exit 1; fi; \
	echo "### all test projects passed"
endef

# Full suite: build the whole solution, then run every test project.
test test-full:
	$(DOTNET) build $(SLN) -c $(CONFIG)
	$(call run_tests,$(TEST_PROJECTS))

# Fast path: build+run only the pure unit-test project. It does NOT build the
# full solution, so it avoids compiling the CLI/GUI layers and the heavy
# Avalonia test host (the project's --no-build test only builds its own
# dependency chain, i.e. the Application layer).
test-quick:
	$(DOTNET) build $(QUICK_TEST_PROJECT) -c $(CONFIG)
	$(call run_tests,$(QUICK_TEST_PROJECT))

test-one:
	@test -n "$(TEST_FILTER)" || (echo "usage: make test-one TEST_FILTER=<xunit filter>" 1>&2 && exit 2)
	$(DOTNET) test $(word 1,$(TEST_PROJECTS)) -c $(CONFIG) --filter "$(TEST_FILTER)"

clean:
	$(DOTNET) clean $(SLN) -c $(CONFIG)

purge:
	$(DOTNET) clean $(SLN) -c Release && $(DOTNET) clean $(SLN) -c Debug
