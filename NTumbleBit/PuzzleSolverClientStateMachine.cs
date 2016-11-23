﻿using NBitcoin;
using NBitcoin.BouncyCastle.Math;
using NBitcoin.Crypto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NTumbleBit
{
	public enum PuzzleSolverClientStates
	{
		Initialized,
		WaitingCommitments,
		WaitingEncryptedFakePuzzleKeys,
		WaitingEncryptedRealPuzzleKeys,
		Completed
	}

	public class PuzzleException : Exception
	{
		public PuzzleException(string message) : base(message)
		{

		}
	}

	public class PuzzleSolverClientStateMachine : PuzzleSolver
	{
		public PuzzleSolverClientStateMachine(RsaPubKey serverKey, Puzzle puzzle) : base(15, 285)
		{
			if(puzzle == null)
				throw new ArgumentNullException("puzzle");
			if(serverKey == null)
				throw new ArgumentNullException("serverKey");
			_ServerKey = serverKey;
			_Puzzle = puzzle;
		}


		private readonly RsaPubKey _ServerKey;
		public RsaPubKey ServerKey
		{
			get
			{
				return _ServerKey;
			}
		}


		private readonly Puzzle _Puzzle;
		public Puzzle Puzzle
		{
			get
			{
				return _Puzzle;
			}
		}


		private PuzzleSolverClientStates _State = PuzzleSolverClientStates.Initialized;
		public PuzzleSolverClientStates State
		{
			get
			{
				return _State;
			}
		}

		public Puzzle[] GeneratePuzzles()
		{
			AssertState(PuzzleSolverClientStates.Initialized);
			List<PuzzleSetElement> puzzles = new List<PuzzleSetElement>();
			for(int i = 0; i < RealPuzzleCount; i++)
			{
				Blind blind = null;
				Puzzle puzzle = Puzzle.Blind(ServerKey, ref blind);
				puzzles.Add(new RealPuzzle(puzzle, blind.ToBlindFactor()));
			}

			for(int i = 0; i < FakePuzzleCount; i++)
			{
				byte[] solution = null;
				Puzzle puzzle = ServerKey.GeneratePuzzle(ref solution);
				puzzles.Add(new FakePuzzle(puzzle, solution));
			}

			var puzzlesArray = puzzles.ToArray();
			NBitcoin.Utils.Shuffle(puzzlesArray, RandomUtils.GetInt32());
			PuzzleSet = new PuzzleSet(puzzlesArray);
			_State = PuzzleSolverClientStates.WaitingCommitments;
			return PuzzleSet.Puzzles.ToArray();
		}


		public PuzzleSolution[] GetFakePuzzleSolutions(PuzzleCommitment[] commitments)
		{
			if(commitments == null)
				throw new ArgumentNullException("commitments");
			if(commitments.Length != TotalPuzzleCount)
				throw new ArgumentException("Expecting " + TotalPuzzleCount + " commitments");
			AssertState(PuzzleSolverClientStates.WaitingCommitments);
			PuzzleCommiments = commitments;
			_State = PuzzleSolverClientStates.WaitingEncryptedFakePuzzleKeys;
			return PuzzleSet.PuzzleElements.OfType<FakePuzzle>()
				.Select(f => new PuzzleSolution(Array.IndexOf(PuzzleSet.PuzzleElements, f), f.Solution))
				.ToArray();
		}

		public BlindFactor[] GetBlindFactors(ChachaKey[] keys)
		{
			if(keys == null)
				throw new ArgumentNullException("keys");
			if(keys.Length != FakePuzzleCount)
				throw new ArgumentException("Expecting " + FakePuzzleCount + " keys");
			AssertState(PuzzleSolverClientStates.WaitingEncryptedFakePuzzleKeys);

			int y = 0;
			for(int i = 0; i < PuzzleCommiments.Length; i++)
			{
				var puzzle = PuzzleSet.PuzzleElements[i] as FakePuzzle;
				if(puzzle != null)
				{
					var key = keys[y++].ToBytes(true);
					var commitment = PuzzleCommiments[i];

					var hash = new uint160(Hashes.RIPEMD160(key, key.Length));
					if(hash != commitment.KeyHash)
					{
						throw new PuzzleException("Commitment hash invalid");
					}
					var solution = Utils.ChachaDecrypt(commitment.EncryptedSolution, key);
					if(!new BigInteger(1, solution).Equals(new BigInteger(1, puzzle.Solution)))
					{
						throw new PuzzleException("Commitment encrypted solution invalid");
					}
				}
			}

			_State = PuzzleSolverClientStates.WaitingEncryptedRealPuzzleKeys;
			return PuzzleSet.PuzzleElements.OfType<RealPuzzle>()
				.Select(p => p.BlindFactor)
				.ToArray();
		}

		public byte[] GetSolution(ChachaKey[] keys)
		{
			if(keys == null)
				throw new ArgumentNullException("keys");
			if(keys.Length != RealPuzzleCount)
				throw new ArgumentException("Expecting " + RealPuzzleCount + " keys");
			AssertState(PuzzleSolverClientStates.WaitingEncryptedRealPuzzleKeys);
			byte[] solution = null;
			RealPuzzle solvedPuzzle = null;
			int y = 0;
			for(int i = 0; i < PuzzleCommiments.Length; i++)
			{
				var puzzle = PuzzleSet.PuzzleElements[i] as RealPuzzle;
				if(puzzle != null)
				{
					var key = keys[y++].ToBytes(true);
					var commitment = PuzzleCommiments[i];

					var hash = new uint160(Hashes.RIPEMD160(key, key.Length));
					if(hash == commitment.KeyHash)
					{
						var decryptedSolution = Utils.ChachaDecrypt(commitment.EncryptedSolution, key);
						if(puzzle.Puzzle.Verify(ServerKey, decryptedSolution))
						{
							solution = decryptedSolution;
							solvedPuzzle = puzzle;
							break;
						}
					}
				}
			}
			if(solution == null)
				throw new PuzzleException("Impossible to find solution to the puzzle");

			solution = ServerKey.Unblind(solution, new Blind(ServerKey, solvedPuzzle.BlindFactor.ToBytes()));
			_State = PuzzleSolverClientStates.Completed;
			return solution;
		}

		public PuzzleCommitment[] PuzzleCommiments
		{
			get;
			private set;
		}

		private void AssertState(PuzzleSolverClientStates state)
		{
			if(state != _State)
				throw new InvalidOperationException("Invalid state, actual " + _State + " while expected is " + state);
		}

		PuzzleSet PuzzleSet
		{
			get;
			set;
		}
	}
}