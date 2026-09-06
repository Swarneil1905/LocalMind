//! HTTP transport for private loopback services. Never inherit a system proxy:
//! it could otherwise receive local prompts and the sidecar bearer token.

pub fn local_client() -> reqwest::Client {
    reqwest::Client::builder()
        .no_proxy()
        .build()
        .expect("could not initialize local HTTP transport")
}
