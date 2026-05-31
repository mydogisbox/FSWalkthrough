# FSWalkthrough

FSWalkthrough is an F# workflow testing library for APIs. It lets you write integration tests that express multi-step API workflows — login, create a user, place an order — with minimal noise. Each test only specifies what matters; everything else flows through sensible defaults.

---

## Philosophy

**Tests should express consumer capabilities, not API mechanics.**

A test named `NewUser CanPlaceOrder` describes an interaction — something a consumer of this system is able to do. The test structure reflects that: set up the actor, perform the interaction, assert the outcome. The HTTP calls, request bodies, and response shapes are implementation details of the interaction, not the point of the test.

**Tests highlight what they are testing and nothing else.**

A test that verifies an order is created with two specific items should say exactly that — and nothing about the email address used to log in, the user's role, or the product's default price. Every field specified in a test is a claim that this field matters to this test. Defaults exist so that claim stays true. The same applies to values that flow between steps — if an order needs the user ID from a prior step, reference it rather than hardcoding it. A hardcoded value is a silent claim that the specific value matters, when usually only the dependency does.

---

## Style guide

- F#: [fsharp-style.md](fsharp-style.md)
