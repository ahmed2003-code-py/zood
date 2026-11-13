package com.zawed.api.ledger;

import java.util.UUID;

import jakarta.validation.Valid;

import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;

import org.springframework.http.HttpStatus;
import org.springframework.security.core.annotation.AuthenticationPrincipal;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;

import com.zawed.api.ledger.LedgerApi.StatementEntryResponse;
import com.zawed.api.ledger.LedgerApi.TopUpRequestBody;
import com.zawed.api.ledger.LedgerApi.TopUpResponse;
import com.zawed.api.ledger.LedgerApi.WalletResponse;
import com.zawed.api.ledger.TopUps.Status;
import com.zawed.api.ledger.TopUps.TopUp;
import com.zawed.api.ledger.Wallets.StatementEntry;
import com.zawed.api.platform.security.Permission;
import com.zawed.api.platform.security.RequiresPermission;
import com.zawed.api.platform.security.ZawedPrincipal;
import com.zawed.api.platform.web.ApiContract;
import com.zawed.api.platform.web.Page;

/**
 * The company portal's wallet (PRD §6.2 Corporate Wallet): balance, statement and top-up requests.
 * The company is always the signed-in user's; row-level security hides every other wallet anyway.
 */
@RestController
@RequestMapping(ApiContract.BASE_PATH + "/company")
@Tag(name = "company portal")
class CompanyWalletController {

	private final Wallets wallets;
	private final TopUps topUps;

	CompanyWalletController(Wallets wallets, TopUps topUps) {
		this.wallets = wallets;
		this.topUps = topUps;
	}

	@GetMapping("/wallet")
	@RequiresPermission(Permission.COMPANY_READ)
	@Operation(operationId = "getMyWallet", summary = "The company's wallet balance and low-balance state")
	WalletResponse wallet(@AuthenticationPrincipal ZawedPrincipal principal) {
		return WalletResponse.of(wallets.get(principal.scope().id()));
	}

	@GetMapping("/wallet/statement")
	@RequiresPermission(Permission.COMPANY_READ)
	@Operation(operationId = "getMyWalletStatement", summary = "Wallet entries, newest first, with the running balance")
	Page<StatementEntryResponse> statement(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) String cursor, @RequestParam(required = false) Integer limit) {
		Page<StatementEntry> page = wallets.statement(principal.scope().id(), cursor, limit);
		return new Page<>(page.items().stream().map(StatementEntryResponse::of).toList(), page.nextCursor());
	}

	@GetMapping("/topups")
	@RequiresPermission(Permission.COMPANY_READ)
	@Operation(operationId = "listMyTopUps", summary = "The company's top-up requests, newest first")
	Page<TopUpResponse> topUps(@AuthenticationPrincipal ZawedPrincipal principal,
			@RequestParam(required = false) Status status, @RequestParam(required = false) String cursor,
			@RequestParam(required = false) Integer limit) {
		Page<TopUp> page = topUps.list(principal.scope().id(), status, cursor, limit);
		return new Page<>(page.items().stream().map(TopUpResponse::of).toList(), page.nextCursor());
	}

	@PostMapping("/topups")
	@ResponseStatus(HttpStatus.CREATED)
	@RequiresPermission(Permission.TOPUP_REQUEST)
	@Operation(operationId = "requestTopUp", summary = "Report a bank transfer and ask Operations to credit it")
	TopUpResponse request(@AuthenticationPrincipal ZawedPrincipal principal, @Valid @RequestBody TopUpRequestBody body) {
		return TopUpResponse.of(topUps.request(principal, principal.scope().id(), body.amountPiastres(),
				body.bankReference(), body.transferDate(), body.note()));
	}

	@PostMapping("/topups/{id}/cancel")
	@RequiresPermission(Permission.TOPUP_REQUEST)
	@Operation(operationId = "cancelTopUp", summary = "Withdraw a pending top-up request")
	TopUpResponse cancel(@AuthenticationPrincipal ZawedPrincipal principal, @PathVariable UUID id) {
		return TopUpResponse.of(topUps.cancel(principal, principal.scope().id(), id));
	}

}
