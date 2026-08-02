# Makefile for the MDPlayer FMP visualization / renderer build.
#
# Targets:
#   make            - build Release and run the unit test suites (default)
#   make build      - build Release
#   make debug      - build Debug
#   make test       - run all three test projects (Release) and build
#   make test-one   - run a single test by --filter (TEST_FILTER=...)
#   make clean      - remove obj/ and bin/ for the FMP projects
#   make purge      - clean the whole solution (including submodule deps)
#
# The CLI entry point after a build is:
#   MDPlayer/src/MDPlayer.Fmp.Cli/bin/Release/net8.0/mdplayer-render
# (the repo-root ./fmp-render wrapper also resolves it).

SLN       := MDPlayer/MDPlayer.Fmp.sln
CONFIG    ?= Release
FRAMEWORK ?= net8.0
CLI_DLL   := MDPlayer/src/MDPlayer.Fmp.Cli/bin/$(CONFIG)/$(FRAMEWORK)/mdplayer-render.dll

TEST_PROJECTS := \
	MDPlayer/tests/MDPlayer.Fmp.Tests/MDPlayer.Fmp.Tests.csproj \
	MDPlayer/tests/MDPlayer.Fmp.Application.Tests/MDPlayer.Fmp.Application.Tests.csproj \
	MDPlayer/tests/MDPlayer.Fmp.Gui.Tests/MDPlayer.Fmp.Gui.Tests.csproj

DOTNET ?= dotnet

.PHONY: all build debug release test test-one clean purge cli

all: build test

build release:
	$(DOTNET) build $(SLN) -c $(CONFIG)

debug:
	$(MAKE) build CONFIG=Debug

cli:
	$(DOTNET) build MDPlayer/src/MDPlayer.Fmp.Cli/MDPlayer.Fmp.Cli.csproj -c $(CONFIG)
	@echo "CLI: $${PWD}/$(CLI_DLL)"

# Run every test project once, then report a summary. Each project is run even
# if an earlier one fails (some il-reflection tests are flaky), and make exits
# nonzero only if at least one project reported failing tests.
test:
	$(DOTNET) build $(SLN) -c $(CONFIG)
	@for p in $(TEST_PROJECTS); do \
		echo "==> dotnet test $$p"; \
		$(DOTNET) test $$p -c $(CONFIG) --no-build; \
		rc=$$?; \
		if [ $$rc -ne 0 ]; then FAILED=1; echo "*** FAILED: $$p"; else echo "*** OK: $$p"; fi; \
	done; \
	if [ "$$FAILED" = "1" ]; then echo "### one or more test projects failed"; exit 1; fi; \
	echo "### all test projects passed"

test-one:
	@test -n "$(TEST_FILTER)" || (echo "usage: make test-one TEST_FILTER=<xunit filter>" 1>&2 && exit 2)
	$(DOTNET) test $(word 1,$(TEST_PROJECTS)) -c $(CONFIG) --filter "$(TEST_FILTER)"

clean:
	$(DOTNET) clean $(SLN) -c $(CONFIG)

purge:
	$(DOTNET) clean $(SLN) -c Release && $(DOTNET) clean $(SLN) -c Debug
