window.wallet = {
    connect: async () => {
        const p = window.phantom?.solana;
        if (!p) throw new Error("Phantom wallet is not installed");
        const r = await p.connect();
        return r.publicKey.toString();
    },
    disconnect: async () => {
        await window.phantom?.solana?.disconnect();
    },
    // Used later: takes an unsigned transaction (base64), returns the signature.
    signAndSend: async (txBase64) => {
        const bytes = Uint8Array.from(atob(txBase64), c => c.charCodeAt(0));
        const tx = solanaWeb3.Transaction.from(bytes);
        const { signature } = await window.phantom.solana.signAndSendTransaction(tx);
        return signature;
    }
};