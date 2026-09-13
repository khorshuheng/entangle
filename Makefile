# Entangle — build a single binary (framework-dependent: needs the .NET 10 SDK
# to build, and the ASP.NET Core 10 runtime on the machine that runs it).
#
#   make                 build publish/<rid>/entangle
#   make test            run the test suite
#   make clean           remove build output
#
# The runtime identifier is detected from the host; override it if needed:
#   make RID=linux-arm64

PROJECT := Entangle.csproj
RID ?= $(shell uname -m | sed -e 's/^x86_64$$/linux-x64/' -e 's/^amd64$$/linux-x64/' \
	-e 's/^aarch64$$/linux-arm64/' -e 's/^arm64$$/linux-arm64/' \
	-e 's/^armv7l$$/linux-arm/' -e 's/^armv6l$$/linux-arm/' \
	-e 's/^riscv64$$/linux-riscv64/' -e 's/^s390x$$/linux-s390x/')
OUT := publish/$(RID)
BIN := $(OUT)/entangle

.PHONY: all build test clean

all: build

build:
	@case "$(RID)" in \
		linux-*) ;; \
		*) echo "error: unrecognised runtime identifier '$(RID)' for architecture '$$(uname -m)'; pass RID=linux-x64 explicitly" >&2; exit 1 ;; \
	esac
	dotnet publish $(PROJECT) -c Release -r $(RID) --self-contained false \
		-p:PublishSingleFile=true \
		-p:IncludeNativeLibrariesForSelfExtract=true \
		-o $(OUT)
	rm -f $(OUT)/*.pdb $(OUT)/*.staticwebassets.endpoints.json $(OUT)/appsettings.Development.json
	@echo
	@echo "built $(BIN) ($$(du -h $(BIN) | cut -f1)); run it with ./$(BIN)"

test:
	dotnet test Entangle.Tests/Entangle.Tests.csproj

clean:
	rm -rf publish bin obj Entangle.Tests/bin Entangle.Tests/obj
