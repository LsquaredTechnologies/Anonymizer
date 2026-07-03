$env:CFLAGS = "/MD"
$env:CXXFLAGS = "/MD"
cargo build --release
mv target/release/Anonymizer_Core.dll target/release/Anonymizer.Core.dll
