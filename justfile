spt_dir := env("TARKOV_DIR", "~/Games/SPTarkov2")
plugin_dir := spt_dir / "BepInEx/plugins/Convoy"

# Build in debug mode
build:
    dotnet build -p:TarkovDir={{spt_dir}} -p:DeployAfterBuild=false

# Build in release mode
release:
    dotnet build -c Release -p:TarkovDir={{spt_dir}} -p:DeployAfterBuild=false

# Build and copy plugin to SPT install
deploy: build
    mkdir -p {{plugin_dir}}
    cp Build/BepInEx/plugins/Convoy/Convoy.dll {{plugin_dir}}/

# Remove build artifacts
clean:
    dotnet clean
    rm -rf Build/
