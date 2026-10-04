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
    // Tells the app when the user switches accounts in Phantom.
    register: (dotnetRef) => {
        const p = window.phantom?.solana;
        if (!p) return;
        p.on('accountChanged', async (pk) => {
            try {
                const key = pk ?? (await p.connect()).publicKey;
                dotnetRef.invokeMethodAsync('OnAccountChanged', key.toString());
            } catch {
                dotnetRef.invokeMethodAsync('OnAccountChanged', null);
            }
        });
        p.on('disconnect', () => dotnetRef.invokeMethodAsync('OnAccountChanged', null));
    },
    signAndSend: async (txBase64, expected) => {
        const p = window.phantom.solana;
        if (p.publicKey?.toString() !== expected)
            throw new Error("Phantom is on a different account than the app. Reconnect your wallet.");
        const bytes = Uint8Array.from(atob(txBase64), c => c.charCodeAt(0));
        const tx = solanaWeb3.Transaction.from(bytes);
        const { signature } = await p.signAndSendTransaction(tx);
        return signature;
    }
};